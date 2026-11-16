// New in B2M: the Revit side of ensure_wall_type and ensure_floor_type (decisions in addin/src/Core/Families/HostTypeRules.cs).
// Drawings give walls and slabs by thickness, so the build needs a Basic Wall or Floor type of exactly that total
// thickness. The order is FIND, then ADAPT A COPY, then ASK:
//   existing   - a candidate type of the requested name, or else any candidate with the same spec (total thickness,
//                wall function, structural layer when asked, layer stack when given) is returned; never edited. A type
//                with the requested name and another spec is TYPE_DIMENSION_MISMATCH.
//   duplicated - a base type (base_type_name, or the closest in thickness; a tie that would change the inherited materials
//                is TYPE_CHOICE_NEEDED) is duplicated to a NEW type; only the new type gets the compound structure (and,
//                for walls, the Function). The base type is never touched.
// Duplicate before edit (see TypeEditGuard): these commands only ever create types, so no type in use can change.
// Materials named in layers must exist (MATERIAL_NOT_FOUND with the options); materials are never created.
// Everything runs in one transaction; any failure (or Revit warning, via FailureScope) leaves the project unchanged.
// They are batch-safe (one plain transaction each).

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using B2M.Revit.Commands.Materials;
using B2M.Revit.Core.Families;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Families
{
    internal static class HostTypes
    {
        private const string Unchanged = " Nothing was changed.";

        public const string LayersSchema =
            @"""layers"":{""type"":""array"",""items"":{""type"":""object""},""description"":""[{function: structure|finish1|finish2|substrate|insulation|membrane, "
            + @"thickness_mm, material?}], outside (walls) or top (floors) first; the sum must equal thickness_mm; a membrane is 0 mm; at least one "
            + @"structure layer. A layer without material keeps the base type's material for that function. Omit for one structure layer of "
            + @"thickness_mm with the base type's core material.""}";

        private static readonly Dictionary<string, MaterialFunctionAssignment> Functions = new Dictionary<string, MaterialFunctionAssignment>
        {
            ["structure"] = MaterialFunctionAssignment.Structure,
            ["substrate"] = MaterialFunctionAssignment.Substrate,
            ["insulation"] = MaterialFunctionAssignment.Insulation,
            ["finish1"] = MaterialFunctionAssignment.Finish1,
            ["finish2"] = MaterialFunctionAssignment.Finish2,
            ["membrane"] = MaterialFunctionAssignment.Membrane
        };

        private static string FunctionName(MaterialFunctionAssignment function)
        {
            foreach (var pair in Functions)
                if (pair.Value == function) return pair.Key;
            return function == MaterialFunctionAssignment.StructuralDeck ? "structural_deck" : "none";
        }

        /// <summary>One ensure_wall_type / ensure_floor_type call from validated params to result.</summary>
        public static CommandResult Ensure(UIApplication app, JObject p, HostKind kind)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            var command = kind == HostKind.Wall ? "ensure_wall_type" : "ensure_floor_type";
            if (doc.IsFamilyDocument) return CommandResult.Fail("NOT_A_PROJECT: " + command + " works on a project document, not a family.");

            if (!TryRequest(p, kind, out var request, out var failure)) return failure;
            var dryRun = p.Value<bool?>("dry_run") ?? false;

            // Materials the caller named must exist; never created.
            var named = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            if (request.Layers != null)
                for (var i = 0; i < request.Layers.Count; i++)
                {
                    var name = request.Layers[i].Material;
                    if (name == null || named.ContainsKey(name)) continue;
                    if (!MaterialsHelpers.TryMaterial(doc, null, name, "material_id", "material", out var material, out var missing))
                        return CommandResult.Fail(missing.Error + " (layers[" + i + "].material). B2M never creates materials: use one of valid_materials, "
                            + "omit material to keep the base type's, or ask the user." + Unchanged, missing.Data);
                    named[name] = material;
                }
            // Compare and report by the material's own spelling.
            if (request.Layers != null)
                foreach (var layer in request.Layers.Where(l => l.Material != null))
                    layer.Material = named[layer.Material].Name;

            var all = AllTypes(doc, kind);
            var candidates = all.Where(t => IsCandidate(t, request)).ToList();
            var otherNames = all.Where(t => !IsCandidate(t, request)).Select(t => t.Name).ToList();
            var facts = candidates.Select(t => Facts(doc, t, kind)).ToList();
            var decision = HostTypeRule.Decide(facts, otherNames, request);

            switch (decision.Step)
            {
                case HostTypeStep.UseExisting:
                    return CommandResult.Ok(Result(request, "existing", candidates[decision.Index], facts[decision.Index], decision, null, dryRun));
                case HostTypeStep.Refuse:
                    var data = decision.Data ?? new JObject();
                    if (decision.Index >= 0) data["type_id"] = RevitCompat.GetId(candidates[decision.Index].Id);
                    return CommandResult.Fail(decision.Error, data);
            }

            var baseType = candidates[decision.BaseIndex];
            if (!TryStructure(doc, request, decision.Layers, named, out var structure, out failure)) return failure;
            if (dryRun)
            {
                var planned = new HostTypeFacts { Name = decision.TypeName, TotalMm = request.ThicknessMm, Layers = decision.Layers };
                return CommandResult.Ok(Result(request, "duplicated", null, planned, decision, baseType, true));
            }

            using (var tx = new Transaction(doc, "B2M: Ensure " + (kind == HostKind.Wall ? "wall" : "floor") + " type"))
            {
                tx.Start();
                try
                {
                    var created = baseType.Duplicate(decision.TypeName) as HostObjAttributes;
                    if (created == null) return RolledBack(tx, "DUPLICATE_FAILED: Revit did not duplicate '" + baseType.Name + "'.");
                    created.SetCompoundStructure(structure);
                    if (created is WallType wallType)
                        wallType.Function = request.Function == "exterior" ? WallFunction.Exterior : WallFunction.Interior;
                    doc.Regenerate();

                    // Read back: the new type must now have exactly the asked spec.
                    var made = Facts(doc, created, kind);
                    var check = new HostTypeRequest
                    {
                        Kind = kind,
                        ThicknessMm = request.ThicknessMm,
                        Function = request.Function,
                        Structural = request.Structural,
                        Foundation = request.Foundation,
                        Layers = decision.Layers
                    };
                    var differences = HostTypeRule.Differences(made, check);
                    if (differences.Count > 0)
                        return RolledBack(tx, "STRUCTURE_NOT_APPLIED: Revit did not take the new structure on '" + decision.TypeName + "': "
                            + string.Join("; ", differences) + ".", new JObject { ["requested"] = new JArray(decision.Layers.Select(l => l.ToJson())) });

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the new type back (a warning or error); see failures." + Unchanged);
                    return CommandResult.Ok(Result(request, "duplicated", created, made, decision, baseType, false));
                }
                catch (Exception ex)
                {
                    return RolledBack(tx, "DUPLICATE_FAILED: could not make type '" + decision.TypeName + "' from '" + baseType.Name + "': " + ex.Message);
                }
            }
        }

        // ---- input ----------------------------------------------------------------------------------

        private static bool IsAbsent(JToken token) => token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined;

        private static bool TryRequest(JObject p, HostKind kind, out HostTypeRequest request, out CommandResult failure)
        {
            request = null;
            failure = null;
            if (!HostTypeRule.TryThickness(p["thickness_mm"], out var thickness, out var error)
                || !HostTypeRule.TryName(p["name"], "name", out var name, out error)
                || !HostTypeRule.TryName(p["base_type_name"], "base_type_name", out var baseName, out error)
                || !HostTypeRule.TryLayers(p["layers"], thickness, out var layers, out error))
            {
                failure = CommandResult.Fail(error);
                return false;
            }
            request = new HostTypeRequest
            {
                Kind = kind,
                ThicknessMm = thickness,
                ExplicitName = name,
                BaseTypeName = baseName,
                Layers = layers
            };
            if (kind == HostKind.Wall)
            {
                var function = IsAbsent(p["function"]) ? "interior" : (p.Value<string>("function") ?? "").Trim().ToLowerInvariant();
                if (!HostTypeRule.WallFunctions.Contains(function))
                {
                    failure = CommandResult.Fail("INVALID_PARAMS: function must be exterior or interior (default interior).");
                    return false;
                }
                request.Function = function;
            }
            else
            {
                request.Structural = p.Value<bool?>("structural") ?? false;
                request.Foundation = p.Value<bool?>("foundation") ?? false;
            }
            return true;
        }

        // ---- facts ----------------------------------------------------------------------------------

        private static List<HostObjAttributes> AllTypes(Document doc, HostKind kind)
        {
            var collector = new FilteredElementCollector(doc).OfClass(kind == HostKind.Wall ? typeof(WallType) : typeof(FloorType));
            return collector.Cast<HostObjAttributes>().OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsCandidate(HostObjAttributes type, HostTypeRequest request)
        {
            if (type is WallType wall) return wall.Kind == WallKind.Basic;
            if (type is FloorType floor) return floor.IsFoundationSlab == request.Foundation;
            return false;
        }

        /// <summary>The type as the decisions see it; Layers null when its structure cannot be read.</summary>
        public static HostTypeFacts Facts(Document doc, HostObjAttributes type, HostKind kind)
        {
            var facts = new HostTypeFacts { Name = type.Name };
            if (type is WallType wall)
            {
                try { facts.Function = wall.Function.ToString().ToLowerInvariant(); }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { facts.Function = null; }
            }
            CompoundStructure cs;
            try { cs = type.GetCompoundStructure(); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { cs = null; }
            if (cs == null) return facts;
            facts.TotalMm = Mm.FromFeet(cs.GetWidth(), 3);
            facts.Structural = cs.StructuralMaterialIndex >= 0;
            facts.Layers = cs.GetLayers().Select(layer =>
            {
                var material = layer.MaterialId == null || layer.MaterialId == ElementId.InvalidElementId ? null : doc.GetElement(layer.MaterialId) as Material;
                return new LayerSpec
                {
                    Function = FunctionName(layer.Function),
                    ThicknessMm = Mm.FromFeet(layer.Width, 3),
                    Material = material?.Name,
                    MaterialId = material == null ? (long?)null : RevitCompat.GetId(material.Id)
                };
            }).ToList();
            return facts;
        }

        // ---- the new structure ----------------------------------------------------------------------

        /// <summary>
        /// The new type's compound structure, built outside any transaction (it is an in-memory object): the planned
        /// layers, the core from the first to the last structure layer, the first structure layer as the structural
        /// material layer. Checked with CompoundStructure.IsValid.
        /// </summary>
        private static bool TryStructure(Document doc, HostTypeRequest request, List<LayerSpec> layers, Dictionary<string, Material> named,
            out CompoundStructure structure, out CommandResult failure)
        {
            structure = null;
            failure = null;
            try
            {
                var revitLayers = new List<CompoundStructureLayer>();
                foreach (var layer in layers)
                {
                    var materialId = ElementId.InvalidElementId;
                    if (layer.Material != null && !layer.Inherited) materialId = named[layer.Material].Id;
                    else if (layer.MaterialId.HasValue) materialId = RevitCompat.ToElementId(layer.MaterialId.Value);
                    revitLayers.Add(new CompoundStructureLayer(Mm.ToFeet(layer.ThicknessMm), Functions[layer.Function], materialId));
                }
                var cs = CompoundStructure.CreateSimpleCompoundStructure(revitLayers);
                HostTypeRule.ShellCounts(layers, out var exterior, out var interior);
                cs.SetNumberOfShellLayers(ShellLayerType.Exterior, exterior);
                cs.SetNumberOfShellLayers(ShellLayerType.Interior, interior);
                var structural = layers.FindIndex(l => l.Function == "structure");
                if (structural >= 0) cs.StructuralMaterialIndex = structural;
                if (!cs.IsValid(doc, out var errors, out _))
                {
                    var problems = errors == null ? new List<string>() : errors.Select(e => "layer " + e.Key + ": " + e.Value).ToList();
                    failure = CommandResult.Fail("STRUCTURE_INVALID: Revit refuses this layer stack (" + (problems.Count == 0 ? "no detail given" : string.Join("; ", problems))
                        + "). Layers thinner than Revit's minimum, or structure layers outside the core, are the usual causes." + Unchanged,
                        new JObject { ["layers"] = new JArray(layers.Select(l => l.ToJson())) });
                    return false;
                }
                structure = cs;
                return true;
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException)
            {
                failure = CommandResult.Fail("STRUCTURE_INVALID: Revit refused the layer stack: " + ex.Message + Unchanged,
                    new JObject { ["layers"] = new JArray(layers.Select(l => l.ToJson())) });
                return false;
            }
        }

        // ---- results --------------------------------------------------------------------------------

        private static CommandResult RolledBack(Transaction tx, string error, JObject data = null)
        {
            if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
            return CommandResult.Fail(error + Unchanged, data);
        }

        private static JObject Result(HostTypeRequest request, string action, Element type, HostTypeFacts facts, HostTypeDecision decision,
            Element baseType, bool dryRun)
        {
            var result = new JObject
            {
                ["type_name"] = type?.Name ?? decision.TypeName,
                ["type_id"] = type == null ? null : (JToken)RevitCompat.GetId(type.Id),
                ["action"] = action,
                ["thickness_mm"] = facts.TotalMm.HasValue ? Math.Round(facts.TotalMm.Value, 1) : request.ThicknessMm,
                ["layers"] = facts.Layers == null ? null : new JArray(facts.Layers.Select(l => l.ToJson())),
                ["base_type"] = baseType?.Name,
                ["base_type_id"] = baseType == null ? null : (JToken)RevitCompat.GetId(baseType.Id)
            };
            if (request.Kind == HostKind.Wall) result["function"] = action == "existing" ? facts.Function : request.Function;
            else
            {
                result["structural"] = action == "existing" ? facts.Structural : facts.Layers?.Any(l => l.Function == "structure") ?? false;
                result["foundation"] = request.Foundation;
            }
            if (action == "existing")
            {
                result["matched_by"] = decision.MatchedBy;
                if (decision.OtherMatches.Count > 0) result["other_matches"] = new JArray(decision.OtherMatches);
                if (request.ExplicitName != null && !string.Equals(request.ExplicitName, decision.TypeName, StringComparison.OrdinalIgnoreCase))
                    result["note"] = "no type is named '" + request.ExplicitName + "', but '" + decision.TypeName + "' has exactly the asked spec, so it is "
                        + "used instead of making a copy.";
            }
            else
            {
                result["base_type_reason"] = decision.BaseReason;
                result["alternatives"] = decision.Alternatives;
                result["inherited_materials"] = new JArray(decision.Layers.Select((l, i) => new { l, i }).Where(x => x.l.Inherited)
                    .Select(x => new JObject { ["layer"] = x.i, ["function"] = x.l.Function, ["material"] = x.l.Material }));
            }
            result["dry_run"] = dryRun;
            return result;
        }
    }

    public sealed class EnsureWallTypeHandler : IRevitCommand
    {
        public string Name => "ensure_wall_type";
        public string Description =>
            "Make sure a Basic Wall type of this total thickness exists, without editing any existing type. FIND: the type named name (default "
            + "'B2M <thickness>mm Exterior|Interior') when its thickness, function and (if given) layers match, else TYPE_DIMENSION_MISMATCH; "
            + "otherwise any Basic Wall type with that spec (action existing). ADAPT A COPY: base_type_name, or the Basic Wall type of that "
            + "function closest in thickness (result: base_type, base_type_reason, alternatives; a tie that would change the inherited materials "
            + "is TYPE_CHOICE_NEEDED: ask the user, pass base_type_name) is duplicated and only the NEW type gets the layers and Function "
            + "(action duplicated). Materials must exist (MATERIAL_NOT_FOUND lists them); none are created. dry_run reports the same and changes nothing.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""thickness_mm"":{""type"":""number"",""description"":""total wall thickness in mm""},
            ""function"":{""type"":""string"",""enum"":[""exterior"",""interior""],""description"":""default interior""},
            ""name"":{""type"":""string"",""description"":""default 'B2M <thickness>mm Exterior' or '... Interior'""},
            ""base_type_name"":{""type"":""string"",""description"":""Basic Wall type to copy; default: the closest in thickness of that function""},
            " + HostTypes.LayersSchema + @",
            ""dry_run"":{""type"":""boolean""}},""required"":[""thickness_mm""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p) => HostTypes.Ensure(app, p, HostKind.Wall);
    }

    public sealed class EnsureFloorTypeHandler : IRevitCommand
    {
        public string Name => "ensure_floor_type";
        public string Description =>
            "Make sure a floor type (or, with foundation: true, a structural foundation slab type) of this total thickness exists, without "
            + "editing any existing type. FIND: the type named name (default 'B2M <thickness>mm Slab'; when a type of the other kind holds that "
            + "name, 'B2M <thickness>mm Foundation Slab' / 'B2M <thickness>mm Floor Slab') when its thickness, structural layer (if structural) and "
            + "(if given) layers match, else TYPE_DIMENSION_MISMATCH; otherwise any type of that kind with that spec (action existing). ADAPT A "
            + "COPY: base_type_name, or the type of that kind closest in thickness (result: base_type, base_type_reason, alternatives; a tie that "
            + "would change the inherited materials is TYPE_CHOICE_NEEDED) is duplicated and only the NEW type gets the layers (action "
            + "duplicated). A foundation slab type can only be copied from another foundation slab type. Materials must exist "
            + "(MATERIAL_NOT_FOUND); none are created. dry_run reports the same and changes nothing.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""thickness_mm"":{""type"":""number"",""description"":""total slab thickness in mm""},
            ""name"":{""type"":""string"",""description"":""default 'B2M <thickness>mm Slab'""},
            ""base_type_name"":{""type"":""string"",""description"":""type of the same kind to copy; default: the closest in thickness""},
            ""structural"":{""type"":""boolean"",""description"":""the type must have a structural layer (default false)""},
            ""foundation"":{""type"":""boolean"",""description"":""a structural foundation slab type (FloorType.IsFoundationSlab), for create_foundation_slab (default false)""},
            " + HostTypes.LayersSchema + @",
            ""dry_run"":{""type"":""boolean""}},""required"":[""thickness_mm""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p) => HostTypes.Ensure(app, p, HostKind.Floor);
    }
}
