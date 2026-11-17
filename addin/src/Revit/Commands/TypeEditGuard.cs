// New in Plinth: the Revit side of "duplicate before edit" (decision in addin/src/Core/Safety/SharedEditRule.cs). Every
// command that can change an existing type (any ElementType), material or loaded family goes through here:
//   1. before any transaction: TryPlan counts who uses each shared thing, applies the rule to duplicate_as /
//      confirm_edit_all, and checks that every copy name is free; a refusal changes nothing;
//   2. inside the command's transaction: Apply duplicates what is to be copied and moves the elements the command
//      named (and only those) onto the copy with Element.ChangeTypeId; the command then edits plan.TargetFor(x);
//   3. Report() goes into the command's result: {duplicated_from, new_type_id | new_material_id, moved_element_ids}
//      for copies, {edited_in_place, instance_count | user_count} for in-place edits.
// Counting. Types: every non-type element whose GetTypeId() is the type (exact). Families: the same over all of the
// family's types. Materials (best effort): placed model elements whose geometry or paint uses the material
// (Element.GetMaterialIds), plus element types whose compound-structure layers or material type parameters reference
// it; elements of such a type count too even when their geometry does not report it. Not counted: object-style
// (category) materials, materials referenced only by an instance parameter that no geometry uses, and other
// documents. When counting throws, the count is unknown and the caller must pass confirm_edit_all: "all".

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Plinth.Revit.Bridge;
using Plinth.Revit.Commands.Families;
using Plinth.Revit.Commands.Materials;
using Plinth.Revit.Core.Safety;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands
{
    /// <summary>One shared thing a command will change, with the elements the command names that use it.</summary>
    internal sealed class SharedEdit
    {
        public SharedEdit(Element shared, IEnumerable<long> namedElementIds = null)
        {
            Shared = shared;
            NamedElementIds = (namedElementIds ?? Enumerable.Empty<long>()).Distinct().ToList();
        }

        /// <summary>An ElementType or a Material.</summary>
        public Element Shared { get; }

        /// <summary>Elements the command targets that use Shared: on a copy, exactly these move onto it.</summary>
        public List<long> NamedElementIds { get; }
    }

    internal sealed class SharedEditPlan
    {
        internal sealed class Entry
        {
            public Element Original;
            public SharedThing Thing;
            public SharedEditDecision Decision;
            public List<long> MoveIds;
            public Element Target;
            public List<long> Moved = new List<long>();
        }

        public List<Entry> Entries { get; } = new List<Entry>();

        public bool Applied { get; private set; }

        /// <summary>The element to edit for original: its copy after Apply, else original itself (also for non-shared elements).</summary>
        public Element TargetFor(Element original)
        {
            if (original == null) return null;
            var entry = Entries.FirstOrDefault(e => e.Original.Id == original.Id);
            return entry?.Target ?? original;
        }

        public Entry EntryFor(Element original) => original == null ? null : Entries.FirstOrDefault(e => e.Original.Id == original.Id);

        /// <summary>
        /// Inside the caller's open transaction: duplicates every copy entry and moves its named elements onto the copy.
        /// Throws on failure so the caller rolls the transaction back.
        /// </summary>
        public void Apply(Document doc)
        {
            if (Applied) return;
            foreach (var entry in Entries)
            {
                if (entry.Decision.Action != SharedEditAction.EditCopy)
                {
                    entry.Target = entry.Original;
                    continue;
                }
                Element copy;
                if (entry.Original is Material material) copy = material.Duplicate(entry.Decision.CopyName);
                else if (entry.Original is ElementType type) copy = type.Duplicate(entry.Decision.CopyName);
                else throw new InvalidOperationException("element " + RevitCompat.GetId(entry.Original.Id) + " cannot be duplicated.");
                if (copy == null)
                    throw new InvalidOperationException("Revit did not duplicate " + SharedEditRule.Describe(entry.Thing) + ".");
                entry.Target = copy;
                if (copy is ElementType)
                {
                    foreach (var id in entry.MoveIds)
                    {
                        var element = doc.GetElement(RevitCompat.ToElementId(id));
                        if (element == null) throw new InvalidOperationException("element " + id + " no longer exists.");
                        var moved = element.ChangeTypeId(copy.Id);
                        entry.Moved.Add(moved != null && moved != ElementId.InvalidElementId ? RevitCompat.GetId(moved) : id);
                    }
                }
            }
            Applied = true;
        }

        public JArray Report()
        {
            var report = new JArray();
            foreach (var entry in Entries)
            {
                var material = entry.Thing.Kind == SharedKind.Material;
                if (entry.Decision.Action == SharedEditAction.EditCopy)
                {
                    var row = new JObject
                    {
                        ["kind"] = material ? "material" : "type",
                        ["duplicated_from"] = entry.Thing.Id,
                        ["duplicated_from_name"] = entry.Thing.Name
                    };
                    row[material ? "new_material_id" : "new_type_id"] = entry.Target == null ? null : (JToken)RevitCompat.GetId(entry.Target.Id);
                    row[material ? "new_material_name" : "new_type_name"] = entry.Target?.Name ?? entry.Decision.CopyName;
                    row["moved_element_ids"] = new JArray(entry.Moved);
                    report.Add(row);
                }
                else
                {
                    var row = new JObject
                    {
                        ["kind"] = entry.Thing.Kind.ToString().ToLowerInvariant(),
                        ["edited_in_place"] = entry.Thing.Id,
                        ["name"] = entry.Thing.Name
                    };
                    row[material ? "user_count" : "instance_count"] = entry.Thing.UserCount.HasValue ? (JToken)entry.Thing.UserCount.Value : null;
                    report.Add(row);
                }
            }
            return report;
        }
    }

    internal static class TypeEditGuard
    {
        /// <summary>Schema properties duplicate_as and confirm_edit_all (no commas around them).</summary>
        public const string SchemaFields = SharedEditRule.SchemaFields;

        public const string Rule = SharedEditRule.RuleText;

        /// <summary>True for what the rule protects: element types and materials.</summary>
        public static bool IsShared(Element element) => element is ElementType || element is Material;

        /// <summary>
        /// Before any transaction: applies the rule to every shared element in edits (non-shared ones are ignored).
        /// On refusal, failure is the command's result and nothing may be changed.
        /// </summary>
        public static bool TryPlan(Document doc, Newtonsoft.Json.Linq.JObject p, IEnumerable<SharedEdit> edits, out SharedEditPlan plan,
            out CommandResult failure, bool copyAllowed = true)
        {
            plan = new SharedEditPlan();
            failure = null;
            var merged = new List<SharedEdit>();
            foreach (var edit in edits.Where(e => e?.Shared != null && IsShared(e.Shared)))
            {
                var existing = merged.FirstOrDefault(m => m.Shared.Id == edit.Shared.Id);
                if (existing == null) merged.Add(new SharedEdit(edit.Shared, edit.NamedElementIds));
                else existing.NamedElementIds.AddRange(edit.NamedElementIds.Where(id => !existing.NamedElementIds.Contains(id)));
            }
            if (merged.Count == 0) return true;

            var types = merged.Select(m => m.Shared).OfType<ElementType>().ToList();
            var materials = merged.Select(m => m.Shared).OfType<Material>().ToList();
            var typeUsers = types.Count == 0 ? new Dictionary<long, List<long>>() : TypeUsers(doc, types.Select(t => t.Id));
            var materialUses = materials.Count == 0 ? new Dictionary<long, MaterialUse>() : MaterialUsers(doc, materials.Select(m => m.Id));

            var duplicateAs = p?[SharedEditRule.DuplicateAsField];
            var confirm = p?[SharedEditRule.ConfirmField];
            var refused = new List<SharedEditDecision>();
            foreach (var edit in merged)
            {
                var id = RevitCompat.GetId(edit.Shared.Id);
                SharedThing thing;
                if (edit.Shared is Material material)
                {
                    var use = materialUses.TryGetValue(id, out var u) ? u : null;
                    thing = new SharedThing
                    {
                        Kind = SharedKind.Material,
                        Id = id,
                        Name = material.Name,
                        UserCount = use?.Total,
                        SampleUserIds = use == null ? new List<long>() : use.Elements.Concat(use.Types).Take(SharedEditRule.MaxSamples).ToList(),
                        CountDetail = new JObject
                        {
                            ["element_count"] = use == null ? null : (JToken)use.Elements.Count,
                            ["type_count"] = use == null ? null : (JToken)use.Types.Count,
                            ["counted"] = use == null
                                ? "the users could not be counted (Revit refused to report them); confirm_edit_all must be \"all\""
                                : "best effort: placed model elements whose geometry or paint uses it, element types whose layers or material type "
                                    + "parameters reference it, and elements of those types"
                        },
                        CopyAllowed = copyAllowed
                    };
                }
                else
                {
                    var type = (ElementType)edit.Shared;
                    var users = typeUsers.TryGetValue(id, out var list) ? list : new List<long>();
                    thing = new SharedThing
                    {
                        Kind = SharedKind.Type,
                        Id = id,
                        Name = type.Name,
                        FamilyName = SafeFamilyName(type),
                        UserCount = users.Count,
                        SampleUserIds = users.Take(SharedEditRule.MaxSamples).ToList(),
                        CopyAllowed = copyAllowed
                    };
                }
                var decision = SharedEditRule.Decide(thing, duplicateAs, confirm, merged.Count);
                if (decision.Action == SharedEditAction.Refuse)
                {
                    var code = decision.Code;
                    if (code != "TYPE_IN_USE" && code != "MATERIAL_IN_USE")
                    {
                        failure = CommandResult.Fail(decision.Error, decision.Data);
                        return false;
                    }
                    refused.Add(decision);
                    continue;
                }
                plan.Entries.Add(new SharedEditPlan.Entry
                {
                    Original = edit.Shared,
                    Thing = thing,
                    Decision = decision,
                    MoveIds = edit.Shared is ElementType ? edit.NamedElementIds : new List<long>()
                });
            }

            if (refused.Count > 0)
            {
                var data = (JObject)refused[0].Data.DeepClone();
                if (refused.Count > 1) data["in_use"] = new JArray(refused.Select(r => r.Data));
                failure = CommandResult.Fail(refused[0].Error + (refused.Count > 1
                    ? " " + (refused.Count - 1) + " more shared type(s)/material(s) in this call need the same choice; see in_use." : ""), data);
                return false;
            }
            return TryCheckCopyNames(doc, plan, out failure);
        }

        /// <summary>A copy name must be free (never reuse or edit an existing type/material of that name) and unique in the call.</summary>
        private static bool TryCheckCopyNames(Document doc, SharedEditPlan plan, out CommandResult failure)
        {
            failure = null;
            var copies = plan.Entries.Where(e => e.Decision.Action == SharedEditAction.EditCopy).ToList();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in copies)
            {
                var name = entry.Decision.CopyName;
                if (!names.Add(name))
                {
                    failure = CommandResult.Fail("INVALID_PARAMS: duplicate_as gives the name '" + name + "' to two copies; each copy needs its own name. "
                        + "Nothing was changed.");
                    return false;
                }
                Element clash;
                if (entry.Original is Material) clash = MaterialsHelpers.ByName(doc, name);
                else clash = FamiliesHelpers.NameClash(doc, (ElementType)entry.Original, name);
                if (clash == null) continue;
                var data = new JObject { [entry.Original is Material ? "existing_material_id" : "existing_type_id"] = RevitCompat.GetId(clash.Id) };
                failure = CommandResult.Fail("NAME_IN_USE: " + (entry.Original is Material ? "a material" : "a type") + " named '" + clash.Name
                    + "' already exists" + (entry.Original is FamilySymbol ? " in this family" : "") + "; duplicate_as needs a new name "
                    + "(an existing type or material is never reused or edited). Nothing was changed.", data);
                return false;
            }
            return true;
        }

        // ---- families (reload) ----------------------------------------------------------------------

        /// <summary>
        /// Reloading a loaded family changes every type in it, so it needs confirm_edit_all = its placed instances (no copy is possible).
        /// </summary>
        public static bool TryConfirmFamilyReload(Document doc, JObject p, Family family, out int instanceCount, out CommandResult failure)
        {
            failure = null;
            var symbolIds = family.GetFamilySymbolIds().ToList();
            var users = symbolIds.Count == 0 ? new Dictionary<long, List<long>>() : TypeUsers(doc, symbolIds);
            var all = users.Values.SelectMany(v => v).Distinct().ToList();
            instanceCount = all.Count;
            var thing = new SharedThing
            {
                Kind = SharedKind.Family,
                Id = RevitCompat.GetId(family.Id),
                Name = family.Name,
                UserCount = all.Count,
                SampleUserIds = all.Take(SharedEditRule.MaxSamples).ToList(),
                CountDetail = new JObject { ["type_count"] = symbolIds.Count },
                CopyAllowed = false
            };
            var decision = SharedEditRule.Decide(thing, null, p?[SharedEditRule.ConfirmField], 1);
            if (decision.Action == SharedEditAction.EditInPlace) return true;
            failure = CommandResult.Fail(decision.Error, decision.Data);
            return false;
        }

        // ---- counting -------------------------------------------------------------------------------

        /// <summary>Placed (non-type) elements of each type, in one pass over the document.</summary>
        public static Dictionary<long, List<long>> TypeUsers(Document doc, IEnumerable<ElementId> typeIds)
        {
            var result = new Dictionary<long, List<long>>();
            foreach (var id in typeIds) result[RevitCompat.GetId(id)] = new List<long>();
            foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                ElementId typeId;
                try { typeId = element.GetTypeId(); }
                catch { continue; }
                if (typeId == null || typeId == ElementId.InvalidElementId) continue;
                if (result.TryGetValue(RevitCompat.GetId(typeId), out var list)) list.Add(RevitCompat.GetId(element.Id));
            }
            return result;
        }

        public static int TypeUserCount(Document doc, ElementType type) => TypeUsers(doc, new[] { type.Id })[RevitCompat.GetId(type.Id)].Count;

        internal sealed class MaterialUse
        {
            public List<long> Elements = new List<long>();
            public List<long> Types = new List<long>();
            public int Total => Elements.Count + Types.Count;
        }

        /// <summary>Best-effort users of each material (see the file header); a material maps to null when counting failed.</summary>
        public static Dictionary<long, MaterialUse> MaterialUsers(Document doc, IEnumerable<ElementId> materialIds)
        {
            var wanted = new HashSet<long>(materialIds.Select(RevitCompat.GetId));
            var result = wanted.ToDictionary(id => id, id => new MaterialUse());
            try
            {
                // Types whose layers or material type parameters reference a wanted material.
                var typeMaterials = new Dictionary<long, HashSet<long>>();
                foreach (var type in new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>())
                {
                    var hits = new HashSet<long>();
                    if (type is HostObjAttributes host)
                    {
                        try
                        {
                            var cs = host.GetCompoundStructure();
                            if (cs != null)
                                for (var i = 0; i < cs.LayerCount; i++)
                                {
                                    var m = RevitCompat.GetId(cs.GetMaterialId(i));
                                    if (wanted.Contains(m)) hits.Add(m);
                                }
                        }
                        catch
                        {
                            // A type without a readable structure has no layers to count.
                        }
                    }
                    foreach (Parameter parameter in type.Parameters)
                    {
                        if (parameter == null || parameter.StorageType != StorageType.ElementId || !parameter.HasValue) continue;
                        var value = parameter.AsElementId();
                        if (value == null || value == ElementId.InvalidElementId) continue;
                        var m = RevitCompat.GetId(value);
                        if (wanted.Contains(m)) hits.Add(m);
                    }
                    if (hits.Count == 0) continue;
                    var typeId = RevitCompat.GetId(type.Id);
                    typeMaterials[typeId] = hits;
                    foreach (var m in hits) result[m].Types.Add(typeId);
                }

                // Placed model elements: their geometry or paint, or their type.
                foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                {
                    var category = element.Category;
                    if (category == null || category.CategoryType != CategoryType.Model) continue;
                    var hits = new HashSet<long>();
                    try
                    {
                        var typeId = element.GetTypeId();
                        if (typeId != null && typeId != ElementId.InvalidElementId && typeMaterials.TryGetValue(RevitCompat.GetId(typeId), out var fromType))
                            hits.UnionWith(fromType);
                        foreach (var id in element.GetMaterialIds(false)) hits.Add(RevitCompat.GetId(id));
                        foreach (var id in element.GetMaterialIds(true)) hits.Add(RevitCompat.GetId(id));
                    }
                    catch
                    {
                        // Elements whose materials Revit cannot report are not counted (documented as best effort).
                    }
                    var elementId = RevitCompat.GetId(element.Id);
                    foreach (var m in hits)
                        if (result.TryGetValue(m, out var use)) use.Elements.Add(elementId);
                }
                return result;
            }
            catch (Exception)
            {
                return wanted.ToDictionary(id => id, id => (MaterialUse)null);
            }
        }

        private static string SafeFamilyName(ElementType type)
        {
            try { return FamiliesHelpers.FamilyNameOf(type) ?? ""; }
            catch { return ""; }
        }
    }
}
