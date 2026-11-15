// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Materials/AssignMaterialToElementHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: without parameter_name the element must have exactly one writable material parameter (or the
// structural material parameter); rvt-mcp took the first ElementId parameter whose English name contained
// "Material", which is a silent guess. Misses list the element's writable material parameters. Parameter.Set's
// result is checked. The compound-layer modes are unchanged: a new duplicated type (default) or, with
// allow_type_mutation, an edit of the shared type that changes every instance of it.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Materials
{
    public sealed class AssignMaterialToElementHandler : IRevitCommand
    {
        private const int MaxIds = 2000;

        public string Name => "assign_material_to_element";
        public string Description =>
            "Assign a material to elements. Parameter mode: sets parameter_name (or the element's only material parameter) on each "
            + "instance. Layer mode (compound_layer_index, walls/floors/roofs/ceilings): duplicates the shared type as duplicate_type_name "
            + "with the layer changed and moves the elements to it, or with allow_type_mutation edits the type itself (every instance changes).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            " + MaterialsHelpers.MaterialSchema + @",
            ""parameter_name"":{""type"":""string""},
            ""compound_layer_index"":{""type"":""integer""},
            ""allow_type_mutation"":{""type"":""boolean""},
            ""duplicate_type_name"":{""type"":""string""}},""required"":[""element_ids""]}";
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
            var mutate = p.Value<bool?>("allow_type_mutation") ?? false;
            var newTypeName = (p.Value<string>("duplicate_type_name") ?? "").Trim();
            if (layer.HasValue && !string.IsNullOrEmpty(parameterName))
                return CommandResult.Fail("INVALID_PARAMS: pass parameter_name or compound_layer_index, not both.");

            var elements = new List<Element>();
            foreach (var id in ids)
            {
                var e = doc.GetElement(RevitCompat.ToElementId(id));
                if (e == null || e is ElementType) return CommandResult.Fail("ELEMENT_NOT_FOUND: element " + id + " is not a placed element in this document.");
                elements.Add(e);
            }

            // Preflight everything before the transaction.
            var parameters = new Dictionary<long, Parameter>();
            HostObjAttributes sharedType = null;
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
                    if (sharedType != null && sharedType.Id != type.Id && !mutate)
                        return CommandResult.Fail("MIXED_TYPES: the elements use more than one type; split the request per type or pass allow_type_mutation: true.");
                    sharedType = type;
                }
                if (!mutate)
                {
                    if (newTypeName.Length == 0)
                        return CommandResult.Fail("INVALID_PARAMS: layer mode needs duplicate_type_name (or allow_type_mutation: true to edit the type in place).");
                    var clash = new FilteredElementCollector(doc).OfClass(sharedType.GetType()).Cast<ElementType>()
                        .FirstOrDefault(t => string.Equals(t.Name, newTypeName, StringComparison.OrdinalIgnoreCase));
                    if (clash != null)
                        return CommandResult.Fail("NAME_IN_USE: a type named '" + clash.Name + "' already exists.",
                            new JObject { ["existing_type_id"] = RevitCompat.GetId(clash.Id) });
                }
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
            using (var tx = new Transaction(doc, "B2M: Assign material"))
            {
                tx.Start();
                try
                {
                    if (layer.HasValue)
                    {
                        var changedTypes = new Dictionary<long, HostObjAttributes>();
                        foreach (var e in elements)
                        {
                            var type = (HostObjAttributes)doc.GetElement(e.GetTypeId());
                            var key = RevitCompat.GetId(type.Id);
                            if (!changedTypes.TryGetValue(key, out var target))
                            {
                                target = mutate ? type : (HostObjAttributes)type.Duplicate(newTypeName);
                                var cs = target.GetCompoundStructure();
                                cs.SetMaterialId(layer.Value, material.Id);
                                target.SetCompoundStructure(cs);
                                changedTypes[key] = target;
                            }
                            if (!mutate) e.ChangeTypeId(target.Id);
                            results.Add(new JObject
                            {
                                ["element_id"] = RevitCompat.GetId(e.Id),
                                ["mode"] = "compound_layer",
                                ["compound_layer_index"] = layer.Value,
                                ["type_id"] = RevitCompat.GetId(target.Id),
                                ["type_name"] = target.Name,
                                ["type_created"] = !mutate
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
                ["results"] = results
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
