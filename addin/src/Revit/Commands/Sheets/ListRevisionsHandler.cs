// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Sheets/ListRevisionsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: revisions come in sequence order (Revision.GetAllRevisionIds); each listed sheet also
// says whether the revision is an additional (manually assigned) one or comes from clouds, and its number on
// that sheet; rows that fail are reported with their error; the silent revision-lookup fallbacks are gone.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Sheets
{
    public sealed class ListRevisionsHandler : IRevitCommand
    {
        public string Name => "list_revisions";
        public string Description =>
            "List project revisions in sequence order (number, date, description, issued, issued to/by) and, unless include_sheets is false, the sheets each appears on.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""include_sheets"":{""type"":""boolean""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            var includeSheets = p.Value<bool?>("include_sheets") ?? true;

            var onSheets = new Dictionary<long, JArray>();
            if (includeSheets)
            {
                foreach (var sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                             .Where(s => !s.IsPlaceholder).OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase))
                {
                    var additional = new HashSet<long>(sheet.GetAdditionalRevisionIds().Select(RevitCompat.GetId));
                    foreach (var revId in sheet.GetAllRevisionIds())
                    {
                        var key = RevitCompat.GetId(revId);
                        if (!onSheets.TryGetValue(key, out var list)) onSheets[key] = list = new JArray();
                        list.Add(new JObject
                        {
                            ["id"] = RevitCompat.GetId(sheet.Id),
                            ["sheet_number"] = sheet.SheetNumber,
                            ["sheet_name"] = sheet.Name,
                            ["revision_number_on_sheet"] = sheet.GetRevisionNumberOnSheet(revId),
                            ["source"] = additional.Contains(key) ? "additional" : "cloud"
                        });
                    }
                }
            }

            var rows = new JArray();
            var failed = new JArray();
            foreach (var id in Revision.GetAllRevisionIds(doc))
            {
                try
                {
                    if (!(doc.GetElement(id) is Revision rev)) continue;
                    var key = RevitCompat.GetId(rev.Id);
                    var row = new JObject
                    {
                        ["id"] = key,
                        ["sequence_number"] = rev.SequenceNumber,
                        ["revision_number"] = rev.RevisionNumber,
                        ["date"] = rev.RevisionDate ?? "",
                        ["description"] = rev.Description ?? "",
                        ["issued"] = rev.Issued,
                        ["issued_to"] = rev.IssuedTo ?? "",
                        ["issued_by"] = rev.IssuedBy ?? ""
                    };
                    if (includeSheets)
                    {
                        onSheets.TryGetValue(key, out var sheets);
                        row["sheets"] = sheets ?? new JArray();
                    }
                    rows.Add(row);
                }
                catch (Exception ex)
                {
                    failed.Add(new JObject { ["id"] = RevitCompat.GetId(id), ["error"] = ex.Message });
                }
            }

            return CommandResult.Ok(new JObject { ["total"] = rows.Count, ["failed"] = failed, ["revisions"] = rows });
        }
    }
}
