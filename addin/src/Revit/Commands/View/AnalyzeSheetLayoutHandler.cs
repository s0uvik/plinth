// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Sheets/AnalyzeSheetLayoutHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: sheet resolution through Lookup.TrySheet (a miss lists the valid sheets; placeholder
// sheets are refused; passing both sheetId and sheetNumber is an error); mm through Mm; every title block on
// the sheet is reported with its family name and its position (rvt-mcp reported only the first one's type
// name and size); viewports also report their box min/max on the sheet; paging through SheetsHelpers.

using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Plinth.Revit.Commands.Sheets;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.View
{
    // Plinth.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class AnalyzeSheetLayoutHandler : IRevitCommand
    {
        public string Name => "analyze_sheet_layout";
        public string Description =>
            "Describe a sheet's layout in sheet millimetres: its title block(s) and a page of viewports with view, scale, "
            + "centre and box size. Sheet by sheetId or sheetNumber, default the active sheet. Page with start_viewport / max_viewports (default 100, max 500).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""sheetNumber"":{""type"":""string""},
            ""sheetId"":{""type"":""integer""},
            ""start_viewport"":{""type"":""integer""},
            ""max_viewports"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var sheetId = p.Value<long?>("sheetId");
            var sheetNumber = p.Value<string>("sheetNumber");
            if (sheetId.HasValue && !string.IsNullOrEmpty(sheetNumber))
                return CommandResult.Fail("INVALID_PARAMS: pass sheetId or sheetNumber, not both.");
            if (!SheetsHelpers.TryPaging(p, "start_viewport", "max_viewports", 100, 500, out var range, out var failure)) return failure;
            if (!Lookup.TrySheet(doc, sheetId, sheetNumber, out var sheet, out failure)) return failure;

            var titleBlocks = new JArray();
            foreach (var tb in SheetsHelpers.TitleBlocksOn(doc, sheet))
            {
                var box = tb.get_BoundingBox(sheet);
                titleBlocks.Add(new JObject
                {
                    ["id"] = RevitCompat.GetId(tb.Id),
                    ["typeId"] = RevitCompat.GetId(tb.GetTypeId()),
                    ["familyName"] = tb.Symbol?.FamilyName,
                    ["typeName"] = tb.Symbol?.Name,
                    ["width"] = box == null ? null : (JToken)Mm.FromFeet(box.Max.X - box.Min.X, 3),
                    ["height"] = box == null ? null : (JToken)Mm.FromFeet(box.Max.Y - box.Min.Y, 3),
                    ["min"] = box == null ? null : new JObject { ["u"] = Mm.FromFeet(box.Min.X, 3), ["v"] = Mm.FromFeet(box.Min.Y, 3) },
                    ["max"] = box == null ? null : new JObject { ["u"] = Mm.FromFeet(box.Max.X, 3), ["v"] = Mm.FromFeet(box.Max.Y, 3) }
                });
            }

            var result = new JObject
            {
                ["sheetId"] = RevitCompat.GetId(sheet.Id),
                ["sheetNumber"] = sheet.SheetNumber,
                ["sheetName"] = sheet.Name,
                ["units"] = "mm, sheet paper space (+u right, +v up)",
                ["titleBlock"] = titleBlocks.Count > 0 ? titleBlocks[0] : null,
                ["titleBlocks"] = titleBlocks
            };

            var ids = sheet.GetAllViewports().OrderBy(RevitCompat.GetId).ToList();
            var page = SheetsHelpers.Page(ids, range, result, "viewport_count", "start_viewport", "returned_viewport_count", "next_viewport");
            var viewports = new JArray();
            foreach (var vpId in page)
            {
                if (!(doc.GetElement(vpId) is Viewport vp)) continue;
                var view = doc.GetElement(vp.ViewId) as View;
                var center = vp.GetBoxCenter();
                var outline = vp.GetBoxOutline();
                viewports.Add(new JObject
                {
                    ["vpId"] = RevitCompat.GetId(vp.Id),
                    ["viewId"] = RevitCompat.GetIdOrNull(view?.Id),
                    ["viewName"] = view?.Name,
                    ["viewType"] = view?.ViewType.ToString(),
                    ["scale"] = view?.Scale,
                    ["center"] = center == null ? null : new JObject { ["u"] = Mm.FromFeet(center.X, 3), ["v"] = Mm.FromFeet(center.Y, 3) },
                    ["width"] = outline == null ? null : (JToken)Mm.FromFeet(outline.MaximumPoint.X - outline.MinimumPoint.X, 3),
                    ["height"] = outline == null ? null : (JToken)Mm.FromFeet(outline.MaximumPoint.Y - outline.MinimumPoint.Y, 3),
                    ["min"] = outline == null ? null : new JObject { ["u"] = Mm.FromFeet(outline.MinimumPoint.X, 3), ["v"] = Mm.FromFeet(outline.MinimumPoint.Y, 3) },
                    ["max"] = outline == null ? null : new JObject { ["u"] = Mm.FromFeet(outline.MaximumPoint.X, 3), ["v"] = Mm.FromFeet(outline.MaximumPoint.Y, 3) }
                });
            }
            result["viewports"] = viewports;
            return CommandResult.Ok(result);
        }
    }
}
