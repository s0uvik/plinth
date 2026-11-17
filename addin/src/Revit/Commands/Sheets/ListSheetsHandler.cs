// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Sheets/ListSheetsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: include_viewports now works (rvt-mcp declared it and ignored it): it adds each sheet's
// viewports with their views; sheets are ordered by number; a limit outside 1..10000 is an error (rvt-mcp
// clamped it); every title block on a sheet is listed; rows that fail are reported with their error rather
// than only counted; the revision lookup no longer falls back silently; the camelCase aliases are gone.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Sheets
{
    // Plinth.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class ListSheetsHandler : IRevitCommand
    {
        public string Name => "list_sheets";
        public string Description =>
            "List sheets ordered by number, with viewport and schedule counts, title blocks and revisions. Filters: number_filter and "
            + "name_pattern (case-insensitive substrings), include_placeholders (default true). include_revisions (default true), "
            + "include_viewports (default false) add detail. limit 1..10000 (default 1000).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""number_filter"":{""type"":""string""},
            ""name_pattern"":{""type"":""string""},
            ""include_revisions"":{""type"":""boolean""},
            ""include_viewports"":{""type"":""boolean""},
            ""include_placeholders"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var numberFilter = p.Value<string>("number_filter");
            var namePattern = p.Value<string>("name_pattern");
            var includeRevisions = p.Value<bool?>("include_revisions") ?? true;
            var includeViewports = p.Value<bool?>("include_viewports") ?? false;
            var includePlaceholders = p.Value<bool?>("include_placeholders") ?? true;
            var limit = p.Value<int?>("limit") ?? 1000;
            if (limit < 1 || limit > 10000) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to 10000.");

            var filtered = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => includePlaceholders || !s.IsPlaceholder)
                .Where(s => SheetsHelpers.ContainsIgnoreCase(s.SheetNumber, numberFilter) && SheetsHelpers.ContainsIgnoreCase(s.Name, namePattern))
                .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var rows = new JArray();
            var failed = new JArray();
            foreach (var sheet in filtered.Take(limit))
            {
                try
                {
                    rows.Add(Row(doc, sheet, includeRevisions, includeViewports));
                }
                catch (Exception ex)
                {
                    failed.Add(new JObject { ["id"] = RevitCompat.GetId(sheet.Id), ["sheet_number"] = sheet.SheetNumber, ["error"] = ex.Message });
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["total"] = filtered.Count,
                ["returned"] = rows.Count,
                ["limit_hit"] = filtered.Count > limit,
                ["failed"] = failed,
                ["sheets"] = rows
            });
        }

        private static JObject Row(Document doc, ViewSheet sheet, bool includeRevisions, bool includeViewports)
        {
            var row = new JObject
            {
                ["id"] = RevitCompat.GetId(sheet.Id),
                ["sheet_number"] = sheet.SheetNumber,
                ["sheet_name"] = sheet.Name,
                ["is_placeholder"] = sheet.IsPlaceholder
            };
            if (sheet.IsPlaceholder)
            {
                row["viewport_count"] = 0;
                row["schedule_count"] = 0;
                row["title_block"] = null;
                row["title_blocks"] = new JArray();
                if (includeRevisions) row["revisions"] = new JArray();
                if (includeViewports) row["viewports"] = new JArray();
                return row;
            }

            var viewportIds = sheet.GetAllViewports();
            row["viewport_count"] = viewportIds.Count;
            row["schedule_count"] = new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>().Count(s => !s.IsTitleblockRevisionSchedule);

            var titleBlocks = new JArray(SheetsHelpers.TitleBlocksOn(doc, sheet).Select(tb => new JObject
            {
                ["instance_id"] = RevitCompat.GetId(tb.Id),
                ["type_id"] = RevitCompat.GetId(tb.GetTypeId()),
                ["family_name"] = tb.Symbol?.FamilyName ?? "",
                ["type_name"] = tb.Symbol?.Name ?? ""
            }));
            row["title_block"] = titleBlocks.Count > 0 ? titleBlocks[0] : null;
            row["title_blocks"] = titleBlocks;

            if (includeRevisions)
            {
                var revisions = new JArray();
                foreach (var id in sheet.GetAllRevisionIds())
                {
                    if (!(doc.GetElement(id) is Revision rev)) continue;
                    revisions.Add(new JObject
                    {
                        ["id"] = RevitCompat.GetId(rev.Id),
                        ["sequence_number"] = rev.SequenceNumber,
                        ["revision_number"] = rev.RevisionNumber,
                        ["revision_number_on_sheet"] = sheet.GetRevisionNumberOnSheet(rev.Id),
                        ["description"] = rev.Description ?? ""
                    });
                }
                row["revisions"] = revisions;
            }

            if (includeViewports)
            {
                row["viewports"] = new JArray(viewportIds.Select(id => doc.GetElement(id) as Viewport).Where(vp => vp != null)
                    .OrderBy(vp => RevitCompat.GetId(vp.Id)).Select(vp =>
                    {
                        var view = doc.GetElement(vp.ViewId) as View;
                        return new JObject
                        {
                            ["viewport_id"] = RevitCompat.GetId(vp.Id),
                            ["view_id"] = RevitCompat.GetId(vp.ViewId),
                            ["view_name"] = view?.Name,
                            ["view_type"] = view?.ViewType.ToString(),
                            ["scale"] = view?.Scale
                        };
                    }));
            }
            return row;
        }
    }
}
