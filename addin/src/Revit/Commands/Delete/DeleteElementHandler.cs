// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Elements/DeleteElementHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: elements Revit removes along with an earlier one (a door with its wall, views with
// their level) count as deleted instead of "not found"; the result names them; the partial-failure
// count is reported as failedCount so batch_execute rolls back a partial delete (rvt-mcp reported
// "failed", which the batch never saw). Limiting deletes to IDs a run created is done by the service,
// which only ever passes IDs from its run store.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Delete
{
    public sealed class DeleteElementHandler : IRevitCommand
    {
        private const int MaxIds = 5000;

        public string Name => "delete_element";
        public string Description => "Delete elements by ID. Elements Revit removes with them (hosted doors, a level's views) are listed in alsoDeleted.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}}},""required"":[""elementIds""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var ids = p["elementIds"].ToObject<long[]>().Distinct().ToArray();
            if (ids.Length == 0) return CommandResult.Fail("INVALID_PARAMS: elementIds must not be empty.");
            if (ids.Length > MaxIds) return CommandResult.Fail("INVALID_PARAMS: at most " + MaxIds + " ids per call.");

            var requested = new HashSet<long>(ids);
            var gone = new HashSet<long>();
            var deleted = new List<long>();
            var failed = new List<long>();
            var errors = new List<string>();

            using (var tx = new Transaction(doc, "B2M: Delete elements"))
            {
                tx.Start();
                foreach (var id in ids)
                {
                    if (gone.Contains(id))
                    {
                        deleted.Add(id);
                        continue;
                    }
                    try
                    {
                        var elementId = RevitCompat.ToElementId(id);
                        if (doc.GetElement(elementId) == null)
                        {
                            failed.Add(id);
                            errors.Add(id + ": not found");
                            continue;
                        }
                        foreach (var removed in doc.Delete(elementId)) gone.Add(RevitCompat.GetId(removed));
                        deleted.Add(id);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(id);
                        errors.Add(id + ": " + ex.Message);
                    }
                }
                if (tx.Commit() != TransactionStatus.Committed)
                    return CommandResult.Fail("NOT_COMMITTED: Revit rolled the delete back; see failures. Nothing was deleted.");
            }

            return CommandResult.Ok(new
            {
                deleted = deleted.Count,
                failedCount = failed.Count,
                total = ids.Length,
                deletedIds = deleted,
                failedIds = failed,
                errors,
                alsoDeleted = gone.Where(id => !requested.Contains(id)).OrderBy(id => id).ToList()
            });
        }
    }
}
