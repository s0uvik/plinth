// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Sheets/PlaceViewOnSheetHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: the viewport centre is given in sheet mm (x_mm, y_mm), defaulting to the centre of the
// title block (rvt-mcp used a fixed point of 1 ft, 0.75 ft); the sheet must exist (create_sheet makes
// one; rvt-mcp silently created a sheet with the first title block) and is found by sheet_id or
// sheet_number; params are snake_case like the other sheet tools; a view already on a sheet names that
// sheet; schedules are pointed to place_schedule_on_sheet; the response reads the viewport box back.
// Step 9 drawing set: anchor "crop" puts the view's crop region centre (the drawing itself) at x_mm/y_mm instead of
// the viewport box centre: the box also holds annotation past the crop (elevation markers, section heads), which
// moved the drawing off its place. title_below_crop puts the viewport title just under the crop region's lower left
// corner (title_gap_mm below it), its line as long as the crop region is wide: by default Revit hangs the title
// under the whole box, far from the drawing and with a line running under everything the box holds.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Sheets
{
    // Plinth.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class PlaceViewOnSheetHandler : IRevitCommand
    {
        private const double DefaultTitleGapMm = 4.0;

        public string Name => "place_view_on_sheet";
        public string Description =>
            "Place a view on a sheet (sheet_id or sheet_number). x_mm/y_mm is the viewport centre in sheet millimetres "
            + "(origin at the sheet origin, +X right, +Y up); default is the centre of the title block. anchor crop (default box) "
            + "puts the centre of the view's crop region there instead (views without an active crop use the box). "
            + "title_below_crop puts the viewport title under the crop region's lower left corner, title_gap_mm (default 4) "
            + "below it, its line as long as the crop region is wide.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""view_id"":{""type"":""integer""},
            ""sheet_id"":{""type"":""integer""},
            ""sheet_number"":{""type"":""string""},
            ""x_mm"":{""type"":""number""},
            ""y_mm"":{""type"":""number""},
            ""anchor"":{""type"":""string"",""enum"":[""box"",""crop""]},
            ""title_below_crop"":{""type"":""boolean""},
            ""title_gap_mm"":{""type"":""number""}},
            ""required"":[""view_id""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            if (!Lookup.TryView(doc, p.Value<long>("view_id"), out View view, out var failure)) return failure;
            if (view is ViewSchedule) return CommandResult.Fail("WRONG_COMMAND: '" + view.Name + "' is a schedule; use place_schedule_on_sheet.");
            if (view is ViewSheet) return CommandResult.Fail("INVALID_PARAMS: '" + view.Name + "' is a sheet; a sheet cannot be placed on a sheet.");
            if (!Lookup.TrySheet(doc, p.Value<long?>("sheet_id"), p.Value<string>("sheet_number"), out var sheet, out failure)) return failure;

            if ((p["x_mm"] == null) != (p["y_mm"] == null))
                return CommandResult.Fail("INVALID_PARAMS: give both x_mm and y_mm, or neither for the title block centre.");
            XYZ center;
            if (p["x_mm"] != null)
            {
                center = new XYZ(Mm.ToFeet(p.Value<double>("x_mm")), Mm.ToFeet(p.Value<double>("y_mm")), 0);
            }
            else
            {
                var frame = ViewportSheetSupport.GetSheetFrame(doc, sheet);
                if (!frame.HasValue)
                    return CommandResult.Fail("NO_TITLEBLOCK: sheet " + sheet.SheetNumber + " has no title block to centre on; pass x_mm and y_mm.");
                center = new XYZ(frame.Value.CenterX, frame.Value.CenterY, 0);
            }

            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
            {
                var placed = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().FirstOrDefault(vp => vp.ViewId == view.Id);
                var onSheet = placed == null ? null : doc.GetElement(placed.SheetId) as ViewSheet;
                return onSheet != null
                    ? CommandResult.Fail("VIEW_ALREADY_PLACED: view '" + view.Name + "' is already on sheet " + onSheet.SheetNumber
                        + "; a view can be on one sheet only (duplicate the view to show it twice).",
                        new { sheet_id = RevitCompat.GetId(onSheet.Id), sheet_number = onSheet.SheetNumber, viewport_id = RevitCompat.GetId(placed.Id) })
                    : CommandResult.Fail("CANNOT_PLACE: Revit does not allow view '" + view.Name + "' (" + view.ViewType
                        + ") on sheet " + sheet.SheetNumber + "; empty legend or drafting views cannot be placed.");
            }

            using (var tx = new Transaction(doc, "Plinth: Place View On Sheet"))
            {
                tx.Start();
                try
                {
                    var viewport = Viewport.Create(doc, sheet.Id, view.Id, center);
                    if (viewport == null) throw new InvalidOperationException("Revit returned no viewport.");
                    var anchorUsed = "box";
                    SheetRect? crop = null;
                    if (p.Value<string>("anchor") == "crop" || p.Value<bool?>("title_below_crop") == true)
                    {
                        doc.Regenerate();
                        crop = ViewportSheetSupport.Read(doc, viewport).Crop;
                    }
                    if (crop.HasValue && p.Value<string>("anchor") == "crop" && p["x_mm"] != null)
                    {
                        double dx = center.X - crop.Value.CenterX, dy = center.Y - crop.Value.CenterY;
                        viewport.SetBoxCenter(viewport.GetBoxCenter() + new XYZ(dx, dy, 0));
                        crop = crop.Value.Translated(dx, dy);
                        anchorUsed = "crop";
                    }
                    string title = null;
                    if (p.Value<bool?>("title_below_crop") == true)
                        title = crop.HasValue
                            ? TitleBelow(doc, viewport, crop.Value, Mm.ToFeet(p.Value<double?>("title_gap_mm") ?? DefaultTitleGapMm))
                            : "left where Revit puts it: the view has no active crop region";
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    var box = viewport.GetBoxOutline();
                    var at = viewport.GetBoxCenter();
                    return CommandResult.Ok(new
                    {
                        viewport_id = RevitCompat.GetId(viewport.Id),
                        sheet_id = RevitCompat.GetId(sheet.Id),
                        sheet_number = sheet.SheetNumber,
                        view_id = RevitCompat.GetId(view.Id),
                        view_name = view.Name,
                        center_sheet_mm = new { x = ViewportSheetSupport.ToMm(at.X), y = ViewportSheetSupport.ToMm(at.Y) },
                        anchor_used = anchorUsed,
                        title_note = title,
                        crop_sheet_mm = ViewportSheetSupport.RectDto(crop),
                        label_sheet_mm = LabelRect(viewport),
                        box_sheet_mm = box == null ? null : ViewportSheetSupport.RectDto(
                            new SheetRect(box.MinimumPoint.X, box.MinimumPoint.Y, box.MaximumPoint.X, box.MaximumPoint.Y))
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("PLACE_FAILED: could not place the view: " + ex.Message + " Nothing was created.");
                }
            }
        }

        /// <summary>
        /// The title's line as long as the drawing is wide, then the whole title (bubble, name, scale, line) moved so its
        /// lower left sits gap below the crop region's lower left. LabelOffset is measured from the box, which includes
        /// annotation past the crop, so the move is worked out from where the title is now. A viewport type without a
        /// title cannot be moved: the view is still placed, and the returned note says why the title was not moved.
        /// </summary>
        private static string TitleBelow(Document doc, Viewport viewport, SheetRect crop, double gap)
        {
            try
            {
                viewport.LabelLineLength = Math.Max(crop.Width, Mm.ToFeet(20));
                doc.Regenerate();
                var label = viewport.GetLabelOutline();
                if (label == null) return "the viewport has no title";
                var height = label.MaximumPoint.Y - label.MinimumPoint.Y;
                var move = new XYZ(crop.MinX - label.MinimumPoint.X, crop.MinY - gap - height - label.MinimumPoint.Y, 0);
                viewport.LabelOffset = viewport.LabelOffset + move;
                doc.Regenerate();
                return null;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                return "not moved: " + ex.Message;
            }
        }

        private static object LabelRect(Viewport viewport)
        {
            try
            {
                var label = viewport.GetLabelOutline();
                return label == null ? null : ViewportSheetSupport.RectDto(new SheetRect(
                    label.MinimumPoint.X, label.MinimumPoint.Y, label.MaximumPoint.X, label.MaximumPoint.Y));
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return null;
            }
        }
    }
}
