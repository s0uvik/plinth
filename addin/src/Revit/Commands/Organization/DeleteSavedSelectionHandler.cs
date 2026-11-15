// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Organization/DeleteSavedSelectionHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: dryRun still defaults to true; a miss or an ambiguous name lists the valid selections;
// the elements in the selection are never deleted, only the saved selection; "B2M: ..." transaction with
// a commit-status check.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Organization
{
    public sealed class DeleteSavedSelectionHandler : IRevitCommand
    {
        public string Name => "delete_saved_selection";
        public string Description =>
            "Delete a saved selection (by selectionId or name). Only the saved selection is removed, never its elements. "
            + "dryRun defaults to TRUE: pass dryRun: false to delete.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""name"":{""type"":""string""},
            ""selectionId"":{""type"":""integer""},
            ""dryRun"":{""type"":""boolean""}}}";
        // Deletes when dryRun is false; the flag is per command, so it is a write.
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var dryRun = p.Value<bool?>("dryRun") ?? true;
            if (!OrganizationHelpers.TrySelection(doc, p.Value<long?>("selectionId"), p.Value<string>("name"), "selectionId", "name",
                    out var selection, out var failure)) return failure;

            var selectionId = RevitCompat.GetId(selection.Id);
            var name = selection.Name;
            var count = selection.GetElementIds().Count;
            var deleted = new List<long>();
            if (!dryRun)
            {
                using (var tx = new Transaction(doc, "B2M: Delete saved selection"))
                {
                    tx.Start();
                    try
                    {
                        deleted.AddRange(doc.Delete(selection.Id).Select(RevitCompat.GetId));
                        if (tx.Commit() != TransactionStatus.Committed)
                            return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures. Nothing was deleted.");
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                        return CommandResult.Fail("DELETE_FAILED: could not delete the saved selection: " + ex.Message);
                    }
                }
            }

            return CommandResult.Ok(new
            {
                dryRun,
                wouldDelete = true,
                deleted = !dryRun,
                selectionId,
                name,
                count,
                deletedElementIds = deleted
            });
        }
    }
}
