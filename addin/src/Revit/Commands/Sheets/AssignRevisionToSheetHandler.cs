// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Sheets/AssignRevisionToSheetHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: an unknown revision lists the valid ones; unknown sheet ids/numbers are one error listing
// all misses with the valid sheets; placeholder sheets are refused; an issued revision is refused before the
// transaction (Revit locks issued revisions on sheets); failures are errors rather than a success with
// updated=false; the silent revision-lookup fallbacks and camelCase aliases are gone.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Sheets
{
    public sealed class AssignRevisionToSheetHandler : IRevitCommand
    {
        public string Name => "assign_revision_to_sheet";
        public string Description =>
            "Add, replace or remove a revision on sheets (sheet_ids and/or sheet_numbers). mode append (default) adds it to the sheet's "
            + "additional revisions, replace makes it the only additional revision, remove takes it off (a revision that comes from "
            + "revision clouds on the sheet stays). Issued revisions cannot be changed.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""revision_id"":{""type"":""integer""},
            ""sheet_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""sheet_numbers"":{""type"":""array"",""items"":{""type"":""string""}},
            ""mode"":{""type"":""string"",""enum"":[""append"",""replace"",""remove""]}},
            ""required"":[""revision_id""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var revisionId = p.Value<long>("revision_id");
            var mode = p.Value<string>("mode") ?? "append";
            var revision = doc.GetElement(RevitCompat.ToElementId(revisionId)) as Revision;
            if (revision == null)
                return CommandResult.Fail("REVISION_NOT_FOUND: no revision has id " + revisionId + ".", new JObject
                {
                    ["valid_revisions"] = new JArray(Revision.GetAllRevisionIds(doc).Select(id => doc.GetElement(id) as Revision)
                        .Where(r => r != null).Select(r => new JObject
                        {
                            ["id"] = RevitCompat.GetId(r.Id),
                            ["revision_number"] = r.RevisionNumber,
                            ["description"] = r.Description,
                            ["issued"] = r.Issued
                        }))
                });
            if (revision.Issued)
                return CommandResult.Fail("REVISION_ISSUED: revision " + revision.RevisionNumber + " is issued, so Revit does not allow adding it to or removing it from sheets. "
                    + "Clear Issued in Revit first.");

            var allSheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase).ToList();
            var targets = new Dictionary<long, ViewSheet>();
            var misses = new List<string>();
            foreach (var id in (p["sheet_ids"] as JArray ?? new JArray()).Select(t => t.Value<long>()))
            {
                var sheet = allSheets.FirstOrDefault(s => RevitCompat.GetId(s.Id) == id);
                if (sheet == null) misses.Add("id " + id);
                else targets[id] = sheet;
            }
            foreach (var number in (p["sheet_numbers"] as JArray ?? new JArray()).Select(t => t.Value<string>()))
            {
                var sheet = allSheets.FirstOrDefault(s => string.Equals(s.SheetNumber, number, StringComparison.OrdinalIgnoreCase));
                if (sheet == null) misses.Add("number '" + number + "'");
                else targets[RevitCompat.GetId(sheet.Id)] = sheet;
            }
            var validSheets = new JObject
            {
                ["valid_sheets"] = new JArray(allSheets.Where(s => !s.IsPlaceholder).Take(100).Select(s => new JObject
                {
                    ["id"] = RevitCompat.GetId(s.Id),
                    ["number"] = s.SheetNumber,
                    ["name"] = s.Name
                }))
            };
            if (misses.Count > 0)
                return CommandResult.Fail("SHEET_NOT_FOUND: no sheet with " + string.Join(", ", misses) + ".", validSheets);
            if (targets.Count == 0)
                return CommandResult.Fail("INVALID_PARAMS: give sheet_ids and/or sheet_numbers.", validSheets);
            var placeholders = targets.Values.Where(s => s.IsPlaceholder).Select(s => s.SheetNumber).ToList();
            if (placeholders.Count > 0)
                return CommandResult.Fail("SHEET_IS_PLACEHOLDER: placeholder sheet(s) " + string.Join(", ", placeholders) + " cannot carry revisions.");

            var rows = new JArray();
            using (var tx = new Transaction(doc, "B2M: Assign Revision To Sheets"))
            {
                tx.Start();
                try
                {
                    foreach (var sheet in targets.Values.OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase))
                    {
                        var ids = new List<ElementId>(sheet.GetAdditionalRevisionIds());
                        if (mode == "replace") ids.Clear();
                        if (mode == "remove") ids.RemoveAll(id => id == revision.Id);
                        else if (!ids.Contains(revision.Id)) ids.Add(revision.Id);
                        sheet.SetAdditionalRevisionIds(ids);

                        var remains = sheet.GetAllRevisionIds().Contains(revision.Id);
                        rows.Add(new JObject
                        {
                            ["sheet_id"] = RevitCompat.GetId(sheet.Id),
                            ["sheet_number"] = sheet.SheetNumber,
                            ["status"] = mode == "remove" ? (remains ? "remains_cloud_derived" : "removed") : mode == "replace" ? "replaced" : "assigned"
                        });
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("ASSIGN_FAILED: could not update the sheets' revisions: " + ex.Message + " Nothing was changed.");
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["revision_id"] = RevitCompat.GetId(revision.Id),
                ["revision_number"] = revision.RevisionNumber,
                ["mode"] = mode,
                ["sheets"] = rows
            });
        }
    }
}
