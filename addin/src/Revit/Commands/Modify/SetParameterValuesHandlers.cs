// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), files
// src/shared/Handlers/Parameters/SetElementParameterValuesHandler.cs and SetTypeParameterValuesHandler.cs, commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for B2M: the two handlers share one setter (ModifyHelpers.TrySet); doubles are read in the parameter's unit
// (length mm, area m2, volume m3, angle degrees; rvt-mcp read area in mm2 and volume in mm3); a name that matches two
// parameters is an error instead of taking the first; old and new values are reported in the same units; elements that
// fail are listed with failedCount, which makes batch_execute roll the batch back; at most 1000 IDs per list.
// Duplicate before edit (TypeEditGuard): set_type_parameter_values never edits a type without duplicate_as (the change
// goes to a new copy and the named elementIds move onto it) or confirm_edit_all (= the type's exact instance count);
// set_element_parameter_values applies the same rule when an id is itself a type or material, and never falls back
// to a type parameter (Element.GetParameters on an instance returns only its own parameters).

using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Modify
{
    public sealed class SetElementParameterValuesHandler : IRevitCommand
    {
        public string Name => "set_element_parameter_values";
        public string Description => "Set one instance parameter on elements by ID. value is text; numbers are in the parameter's unit "
            + "(mm, m2, m3, degrees). valueType auto|string|integer|double|elementId (default auto). Only the elements' own parameters "
            + "are set, never their type's (use set_type_parameter_values). An id that is itself a type or material is guarded:" + TypeEditGuard.Rule;
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""parameterName"":{""type"":""string""},
            ""value"":{""type"":""string""},
            ""valueType"":{""type"":""string"",""enum"":[""auto"",""string"",""integer"",""double"",""elementId""]},
            " + TypeEditGuard.SchemaFields + @"},
            ""required"":[""elementIds"",""parameterName"",""value""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail(ModifyHelpers.NoDocument);
            if (!ModifyHelpers.TryIds(p, "elementIds", 1000, out var ids, out var failure)) return failure;
            var targets = ids.Select(id => (id, element: doc.GetElement(RevitCompat.ToElementId(id)), sources: (List<long>)null)).ToList();
            if (!TypeEditGuard.TryPlan(doc, p, targets.Where(t => t.element != null).Select(t => new SharedEdit(t.element)), out var plan, out failure))
                return failure;
            return ParameterSetter.Run(doc, "Set element parameter values", p, targets, "elementId", plan);
        }
    }

    public sealed class SetTypeParameterValuesHandler : IRevitCommand
    {
        public string Name => "set_type_parameter_values";
        public string Description => "Set one type parameter on element types given by typeIds and/or by the types of elementIds. "
            + "Numbers are in the parameter's unit (mm, m2, m3, degrees)." + TypeEditGuard.Rule
            + " With duplicate_as each type is copied, the copy gets the value and only the given elementIds of that type move onto it. "
            + "Several types in one call: duplicate_as {\"<typeId>\": \"name\"} and confirm_edit_all {\"<typeId>\": N}.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""typeIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""parameterName"":{""type"":""string""},
            ""value"":{""type"":""string""},
            ""valueType"":{""type"":""string"",""enum"":[""auto"",""string"",""integer"",""double"",""elementId""]},
            " + TypeEditGuard.SchemaFields + @"},
            ""required"":[""parameterName"",""value""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail(ModifyHelpers.NoDocument);
            var elementIds = p["elementIds"]?.ToObject<long[]>().Distinct().ToArray() ?? new long[0];
            var typeIds = p["typeIds"]?.ToObject<long[]>().Distinct().ToArray() ?? new long[0];
            if (elementIds.Length + typeIds.Length == 0) return CommandResult.Fail("INVALID_PARAMS: give typeIds or elementIds.");
            if (elementIds.Length > 1000 || typeIds.Length > 1000) return CommandResult.Fail("INVALID_PARAMS: at most 1000 IDs per list.");

            var missingElements = new List<long>();
            var sources = new Dictionary<long, List<long>>();
            var order = new List<long>();
            foreach (var id in elementIds)
            {
                var element = doc.GetElement(RevitCompat.ToElementId(id));
                var typeId = element?.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId)
                {
                    missingElements.Add(id);
                    continue;
                }
                var key = RevitCompat.GetId(typeId);
                if (!sources.ContainsKey(key))
                {
                    sources[key] = new List<long>();
                    order.Add(key);
                }
                sources[key].Add(id);
            }
            order.AddRange(typeIds.Where(id => !sources.ContainsKey(id)));
            if (missingElements.Count > 0)
                return CommandResult.Fail("ELEMENT_NOT_FOUND: some elementIds do not exist or have no type. Nothing was changed.",
                    new { missingElementIds = missingElements });

            var targets = order.Select(id => (id, element: doc.GetElement(RevitCompat.ToElementId(id)) as Element,
                sources: sources.TryGetValue(id, out var s) ? s : null)).ToList();
            var bad = targets.Where(t => !(t.element is ElementType)).Select(t => t.id).ToList();
            if (bad.Count > 0)
                return CommandResult.Fail("TYPE_NOT_FOUND: some typeIds are not element types. Nothing was changed.", new { invalidTypeIds = bad });
            if (!TypeEditGuard.TryPlan(doc, p, targets.Select(t => new SharedEdit(t.element, t.sources)), out var plan, out var failure))
                return failure;
            return ParameterSetter.Run(doc, "Set type parameter values", p, targets, "typeId", plan);
        }
    }

    internal static class ParameterSetter
    {
        public static CommandResult Run(Document doc, string title, JObject p, List<(long id, Element element, List<long> sources)> targets, string idKey,
            SharedEditPlan plan)
        {
            var name = p.Value<string>("parameterName");
            var value = p.Value<string>("value");
            var valueType = p.Value<string>("valueType") ?? "auto";
            var updated = new JArray();
            var failed = new JArray();

            var result = ModifyHelpers.InTransaction(doc, title, () =>
            {
                plan.Apply(doc);
                foreach (var (id, original, sources) in targets)
                {
                    var element = plan.TargetFor(original);
                    var row = new JObject { [idKey] = id };
                    if (sources != null) row["sourceElementIds"] = new JArray(sources);
                    if (element == null)
                    {
                        row["error"] = "not found";
                        failed.Add(row);
                        continue;
                    }
                    if (element.Id != original.Id) row["editedCopyId"] = RevitCompat.GetId(element.Id);
                    row["name"] = element.Name;
                    row["category"] = element.Category?.Name;
                    var parameter = ModifyHelpers.FindParameter(element, name, out var findError);
                    if (parameter == null && element.Id != original.Id)
                        return CommandResult.Fail("SET_FAILED: the copy '" + element.Name + "' of " + idKey + " " + id + ": " + findError
                            + ". The copy is not kept. Nothing was changed.");
                    if (parameter == null)
                    {
                        row["error"] = findError;
                        failed.Add(row);
                        continue;
                    }
                    row["storageType"] = parameter.StorageType.ToString();
                    row["oldValue"] = ModifyHelpers.Value(parameter);
                    row["oldDisplayValue"] = ModifyHelpers.Display(parameter);
                    string error;
                    try
                    {
                        error = ModifyHelpers.TrySet(parameter, value, valueType, row);
                    }
                    catch (System.Exception ex)
                    {
                        error = ex.Message;
                    }
                    row["newValue"] = ModifyHelpers.Value(parameter);
                    row["newDisplayValue"] = ModifyHelpers.Display(parameter);
                    if (error != null && element.Id != original.Id)
                        return CommandResult.Fail("SET_FAILED: the copy '" + element.Name + "' of " + idKey + " " + id + " did not take the value: " + error
                            + ". The copy is not kept. Nothing was changed.", new { failed = new JArray(row) });
                    if (error != null)
                    {
                        row["error"] = error;
                        failed.Add(row);
                    }
                    else updated.Add(row);
                }
                return updated.Count == 0
                    ? CommandResult.Fail("NOTHING_SET: no target accepted the value. Nothing was changed.", new { failed })
                    : CommandResult.Ok(null);
            });
            if (!result.Success) return result;
            return CommandResult.Ok(new JObject
            {
                ["parameterName"] = name,
                ["value"] = value,
                ["valueType"] = valueType,
                ["requested"] = targets.Count,
                ["updatedCount"] = updated.Count,
                ["failedCount"] = failed.Count,
                ["updated"] = updated,
                ["failed"] = failed,
                ["shared_edits"] = plan.Report()
            });
        }
    }
}
