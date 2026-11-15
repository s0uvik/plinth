// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Sheets/GetViewportGeometryHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: sheet resolution through Lookup.TrySheet (a miss lists the valid sheets; placeholder
// sheets are refused; both sheet_id and sheet_number is an error); viewport ids not on the sheet are an
// error listing the ones that are; paging through SheetsHelpers; the snapshot DTO lives in SheetsHelpers
// because B2M's ViewportSheetSupport dropped it.

using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Sheets
{
    public sealed class GetViewportGeometryHandler : IRevitCommand
    {
        public string Name => "get_viewport_geometry";
        public string Description =>
            "Read viewport positions on a sheet (sheet_id or sheet_number, default the active sheet) in sheet millimetres: visible crop "
            + "rectangle, view outline, centre, scale, rotation, pinned and crop flags. Optional viewport_ids; paged by start_viewport / max_viewports (default 100, max 500).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""sheet_id"":{""type"":""integer""},
            ""sheet_number"":{""type"":""string""},
            ""viewport_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""start_viewport"":{""type"":""integer""},
            ""max_viewports"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var sheetId = p.Value<long?>("sheet_id");
            var sheetNumber = p.Value<string>("sheet_number");
            if (sheetId.HasValue && !string.IsNullOrEmpty(sheetNumber))
                return CommandResult.Fail("INVALID_PARAMS: pass sheet_id or sheet_number, not both.");
            if (!SheetsHelpers.TryPaging(p, "start_viewport", "max_viewports", 100, 500, out var range, out var failure)) return failure;
            if (!Lookup.TrySheet(doc, sheetId, sheetNumber, out var sheet, out failure)) return failure;

            var viewports = ViewportSheetSupport.GetViewports(doc, sheet);
            if (p["viewport_ids"] is JArray idArray)
            {
                if (idArray.Count == 0) return CommandResult.Fail("INVALID_PARAMS: viewport_ids cannot be empty when given.");
                var wanted = new HashSet<long>(idArray.Select(t => t.Value<long>()));
                var onSheet = viewports.Select(vp => RevitCompat.GetId(vp.Id)).ToList();
                var unknown = wanted.Where(id => !onSheet.Contains(id)).OrderBy(id => id).ToList();
                if (unknown.Count > 0)
                    return CommandResult.Fail("VIEWPORT_NOT_ON_SHEET: viewport id(s) " + string.Join(", ", unknown) + " are not on sheet " + sheet.SheetNumber + ".",
                        new JObject { ["valid_viewport_ids"] = new JArray(onSheet) });
                viewports = viewports.Where(vp => wanted.Contains(RevitCompat.GetId(vp.Id))).ToList();
            }

            var frame = ViewportSheetSupport.GetSheetFrame(doc, sheet);
            var result = new JObject
            {
                ["sheet_id"] = RevitCompat.GetId(sheet.Id),
                ["sheet_number"] = sheet.SheetNumber,
                ["sheet_name"] = sheet.Name,
                ["units"] = "mm",
                ["coordinate_frame"] = ViewportSheetSupport.CoordinateFrame,
                ["alignment_rect_note"] = ViewportSheetSupport.AlignmentRectNote,
                ["sheet_frame_sheet_mm"] = frame.HasValue ? JToken.FromObject(ViewportSheetSupport.RectDto(frame)) : null
            };
            var page = SheetsHelpers.Page(viewports, range, result, "viewport_count", "start_viewport", "returned_viewport_count", "next_viewport");
            result["viewports"] = new JArray(page.Select(vp => JToken.FromObject(SheetsHelpers.SnapshotDto(ViewportSheetSupport.Read(doc, vp)))));
            return CommandResult.Ok(result);
        }
    }
}
