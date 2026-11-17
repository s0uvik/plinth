// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Materials/AssignMaterialToElementHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: without parameter_name the element must have exactly one writable material parameter (or the
// structural material parameter); rvt-mcp took the first ElementId parameter whose English name contained
// "Material", which is a silent guess. Misses list the element's writable material parameters. Parameter.Set's
// result is checked. Layer mode follows the shared duplicate-before-edit rule (TypeEditGuard): duplicate_as copies each
// type, changes the layer on the copy and moves only the given elements onto it; confirm_edit_all (= the type's exact
// instance count) edits the type in place; without either it refuses with TYPE_IN_USE. This replaces the earlier
// duplicate_type_name (now duplicate_as, same meaning) and allow_type_mutation (now confirm_edit_all with the count);
// elements of several types are allowed with the object forms {"<typeId>": ...}.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Plinth.Revit.Core.Safety;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Materials
{
    public sealed class AssignMaterialToElementHandler : IRevitCommand
    {
        private const int MaxIds = 2000;

        public string Name => "assign_material_to_element";
        public string Description =>
            "Assign a material to elements. Parameter mode: sets parameter_name (or the element's only material parameter) on each "
            + "instance. Layer mode (compound_layer_index, walls/floors/roofs/ceilings) changes the elements' type:" + TypeEditGuard.Rule
            + " With duplicate_as the copy gets the layer material and only element_ids move onto it. Elements of several types: "
            + "duplicate_as {\"<typeId>\": \"name\"} or confirm_edit_all {\"<typeId>\": N}.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            " + MaterialsHelpers.MaterialSchema + @",
            ""parameter_name"":{""type"":""string""},
            ""compound_layer_index"":{""type"":""integer""},
            " + TypeEditGuard.SchemaFields + @"},""required"":[""element_ids""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var ids = p["element_ids"].ToObject<long[]>().Distinct().ToArray();
            if (ids.Length == 0 || ids.Length > MaxIds) return CommandResult.Fail("INVALID_PARAMS: element_ids must hold 1 to " + MaxIds + " ids.");
            if (!MaterialsHelpers.TryMaterial(doc, p.Value<long?>("material_id"), p.Value<string>("material_name"),
                    "material_id", "material_name", out var material, out var failure)) return failure;
            var parameterName = p.Value<string>("parameter_name");
            var layer = p.Value<int?>("compound_layer_index");
            if (layer.HasValue && !string.IsNullOrEmpty(parameterName))
                return CommandResult.Fail("INVALID_PARAMS: pass parameter_name or compound_layer_index, not both.");
            if (!layer.HasValue && (p[SharedEditRule.DuplicateAsField] != null || p[SharedEditRule.ConfirmField] != null))
                return CommandResult.Fail("INVALID_PARAMS: duplicate_as and confirm_edit_all apply to layer mode (compound_layer_index) only; "
                    + "parameter mode sets the elements' own parameters and changes no type.");

            var elements = new List<Element>();
            foreach (var id in ids)
            {
                var e = doc.GetElement(RevitCompat.ToElementId(id));
                if (e == null || TypeEditGuard.IsShared(e)) return CommandResult.Fail("ELEMENT_NOT_FOUND: element " + id + " is not a placed element in this document (types and materials are not assigned here).");
                elements.Add(e);
            }

            // Preflight everything before the transaction.
            var parameters = new Dictionary<long, Parameter>();
            var plan = new SharedEditPlan();
            if (layer.HasValue)
            {
                foreach (var e in elements)
                {
                    var type = doc.GetElement(e.GetTypeId()) as HostObjAttributes;
                    var cs = type?.GetCompoundStructure();
                    if (cs == null)
                        return CommandResult.Fail("NO_LAYERS: element " + RevitCompat.GetId(e.Id) + " has no layered type (walls, floors, roofs, ceilings).");
                    if (layer.Value < 0 || layer.Value >= cs.LayerCount)
                        return CommandResult.Fail("INVALID_PARAMS: compound_layer_index " + layer.Value + " is outside 0.." + (cs.LayerCount - 1)
                            + " for element " + RevitCompat.GetId(e.Id) + ".");
                }
                var edits = elements.GroupBy(e => RevitCompat.GetId(e.GetTypeId()))
                    .Select(g => new SharedEdit(doc.GetElement(g.First().GetTypeId()), g.Select(e => RevitCompat.GetId(e.Id))));
                if (!TypeEditGuard.TryPlan(doc, p, edits, out plan, out failure)) return failure;
            }
            else
            {
                foreach (var e in elements)
                {
                    var candidates = MaterialParameters(e);
                    Parameter chosen;
                    if (!string.IsNullOrEmpty(parameterName))
                        chosen = candidates.FirstOrDefault(c => string.Equals(c.Definition.Name, parameterName, StringComparison.OrdinalIgnoreCase));
                    else
                    {
                        var structural = e.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
                        chosen = structural != null && !structural.IsReadOnly ? structural : candidates.Count == 1 ? candidates[0] : null;
                    }
                    if (chosen == null)
                        return CommandResult.Fail((string.IsNullOrEmpty(parameterName)
                                ? (candidates.Count == 0 ? "NO_MATERIAL_PARAMETER: element " + RevitCompat.GetId(e.Id) + " has no writable material parameter on the instance (type materials need the type, or layer mode)."
                                    : "PARAMETER_AMBIGUOUS: element " + RevitCompat.GetId(e.Id) + " has several material parameters; pass parameter_name.")
                                : "PARAMETER_NOT_FOUND: element " + RevitCompat.GetId(e.Id) + " has no writable material parameter named '" + parameterName + "'."),
                            new JObject { ["material_parameters"] = new JArray(candidates.Select(c => c.Definition.Name).Distinct()) });
                    parameters[RevitCompat.GetId(e.Id)] = chosen;
                }
            }

            var results = new JArray();
            using (var tx = new Transaction(doc, "Plinth: Assign material"))
            {
                tx.Start();
                try
                {
                    if (layer.HasValue)
                    {
                        // Copies are made and the elements moved onto them first; then each edited type gets the layer once.
                        var originals = elements.ToDictionary(e => RevitCompat.GetId(e.Id), e => (HostObjAttributes)doc.GetElement(e.GetTypeId()));
                        plan.Apply(doc);
                        var changedTypes = new Dictionary<long, HostObjAttributes>();
                        foreach (var e in elements)
                        {
                            var type = originals[RevitCompat.GetId(e.Id)];
                            var key = RevitCompat.GetId(type.Id);
                            var created = plan.EntryFor(type)?.Decision.Action == SharedEditAction.EditCopy;
                            if (!changedTypes.TryGetValue(key, out var target))
                            {
                                target = (HostObjAttributes)plan.TargetFor(type);
                                var cs = target.GetCompoundStructure();
                                cs.SetMaterialId(layer.Value, material.Id);
                                target.SetCompoundStructure(cs);
                                changedTypes[key] = target;
                            }
                            results.Add(new JObject
                            {
                                ["element_id"] = RevitCompat.GetId(e.Id),
                                ["mode"] = "compound_layer",
                                ["compound_layer_index"] = layer.Value,
                                ["type_id"] = RevitCompat.GetId(target.Id),
                                ["type_name"] = target.Name,
                                ["type_created"] = created
                            });
                        }
                    }
                    else
                    {
                        foreach (var e in elements)
                        {
                            var parameter = parameters[RevitCompat.GetId(e.Id)];
                            if (!parameter.Set(material.Id))
                            {
                                tx.RollBack();
                                return CommandResult.Fail("SET_FAILED: Revit rejected the material for element " + RevitCompat.GetId(e.Id) + ". Nothing changed.");
                            }
                            results.Add(new JObject
                            {
                                ["element_id"] = RevitCompat.GetId(e.Id),
                                ["mode"] = "parameter",
                                ["parameter_name"] = parameter.Definition.Name
                            });
                        }
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("ASSIGN_FAILED: " + ex.Message + " Nothing changed.");
                }
            }
            return CommandResult.Ok(new JObject
            {
                ["updated"] = true,
                ["material_id"] = RevitCompat.GetId(material.Id),
                ["material_name"] = material.Name,
                ["results"] = results,
                ["shared_edits"] = plan.Report()
            });
        }

        /// <summary>Writable instance parameters that hold a material.</summary>
        private static List<Parameter> MaterialParameters(Element e)
        {
            var list = new List<Parameter>();
            foreach (Parameter parameter in e.Parameters)
            {
                if (parameter?.Definition == null || parameter.IsReadOnly || parameter.StorageType != StorageType.ElementId) continue;
                bool isMaterial;
                try { isMaterial = SpecTypeId.Reference.Material.Equals(parameter.Definition.GetDataType()); }
                catch { isMaterial = false; }
                if (!isMaterial)
                {
                    var current = parameter.AsElementId();
                    isMaterial = current != null && current != ElementId.InvalidElementId && e.Document.GetElement(current) is Material;
                }
                if (isMaterial) list.Add(parameter);
            }
            return list;
        }
    }
}
