// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Organization/AssignElementsToWorksetHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: a workset that is not found lists the valid user worksets; worksetId and worksetName may not
// disagree; elements that cannot move are listed with failedCount so batch_execute rolls back; elements already on
// the workset count as unchanged without a write; at most 5000 IDs.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Modify
{
    public sealed class AssignElementsToWorksetHandler : IRevitCommand
    {
        public string Name => "assign_elements_to_workset";
        public string Description => "Move elements to a user workset given by worksetId or worksetName (workshared models only).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""worksetId"":{""type"":""integer""},
            ""worksetName"":{""type"":""string""}},""required"":[""elementIds""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail(ModifyHelpers.NoDocument);
            if (!doc.IsWorkshared) return CommandResult.Fail("NOT_WORKSHARED: this model has no worksets.");
            if (!ModifyHelpers.TryIds(p, "elementIds", 5000, out var ids, out var failure)) return failure;
            var wantedId = p.Value<long?>("worksetId");
            var wantedName = p.Value<string>("worksetName");
            if (!wantedId.HasValue && string.IsNullOrWhiteSpace(wantedName)) return CommandResult.Fail("INVALID_PARAMS: give worksetId or worksetName.");

            var user = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().OrderBy(w => w.Name).ToList();
            var workset = user.FirstOrDefault(w => (!wantedId.HasValue || w.Id.IntegerValue == wantedId.Value)
                && (string.IsNullOrWhiteSpace(wantedName) || string.Equals(w.Name, wantedName, StringComparison.OrdinalIgnoreCase)));
            if (workset == null)
                return CommandResult.Fail("WORKSET_NOT_FOUND: no user workset matches" + (wantedId.HasValue ? " id " + wantedId.Value : "")
                    + (string.IsNullOrWhiteSpace(wantedName) ? "" : " name '" + wantedName + "'") + ".",
                    new JObject { ["valid_worksets"] = new JArray(user.Select(w => new JObject { ["id"] = w.Id.IntegerValue, ["name"] = w.Name })) });

            var table = doc.GetWorksetTable();
            var updated = new JArray();
            var failed = new JArray();
            var result = ModifyHelpers.InTransaction(doc, "Assign elements to workset", () =>
            {
                foreach (var id in ids)
                {
                    var row = new JObject { ["elementId"] = id };
                    var element = doc.GetElement(RevitCompat.ToElementId(id));
                    if (element == null)
                    {
                        row["error"] = "not found";
                        failed.Add(row);
                        continue;
                    }
                    var old = element.WorksetId;
                    row["oldWorksetId"] = old?.IntegerValue;
                    row["oldWorksetName"] = old == null ? null : table.GetWorkset(old)?.Name;
                    if (old == workset.Id)
                    {
                        row["changed"] = false;
                        updated.Add(row);
                        continue;
                    }
                    var parameter = element.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    try
                    {
                        if (parameter == null || parameter.IsReadOnly) row["error"] = "the element's workset cannot be changed";
                        else if (!parameter.Set(workset.Id.IntegerValue)) row["error"] = "Revit rejected the workset";
                    }
                    catch (Exception ex)
                    {
                        row["error"] = ex.Message;
                    }
                    if (row["error"] != null) failed.Add(row);
                    else
                    {
                        row["changed"] = true;
                        updated.Add(row);
                    }
                }
                return updated.Count == 0
                    ? CommandResult.Fail("NOTHING_CHANGED: no element could move to the workset. Nothing was changed.", new { failed })
                    : CommandResult.Ok(null);
            });
            if (!result.Success) return result;
            return CommandResult.Ok(new JObject
            {
                ["newWorksetId"] = workset.Id.IntegerValue,
                ["newWorksetName"] = workset.Name,
                ["requested"] = ids.Length,
                ["updatedCount"] = updated.Count,
                ["failedCount"] = failed.Count,
                ["updated"] = updated,
                ["failed"] = failed
            });
        }
    }
}
