// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Sheets/PlaceScheduleOnSheetHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: mm go through Mm (no literal 304.8); sheet and schedule misses list the valid options;
// failures are errors rather than a success with placed=false; the camelCase aliases are gone (the
// schema rejects them); the response reads the placed point and the schedule's box on the sheet back.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Sheets
{
    public sealed class PlaceScheduleOnSheetHandler : IRevitCommand
    {
        public string Name => "place_schedule_on_sheet";
        public string Description =>
            "Place a schedule (schedule_id or schedule_name) on a sheet (sheet_id or sheet_number; default the active sheet). "
            + "x_mm/y_mm is the schedule's insertion point (its top-left corner) in sheet millimetres.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""sheet_id"":{""type"":""integer""},
            ""sheet_number"":{""type"":""string""},
            ""schedule_id"":{""type"":""integer""},
            ""schedule_name"":{""type"":""string""},
            ""x_mm"":{""type"":""number""},
            ""y_mm"":{""type"":""number""}},
            ""required"":[""x_mm"",""y_mm""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var xMm = p.Value<double>("x_mm");
            var yMm = p.Value<double>("y_mm");
            if (double.IsNaN(xMm) || double.IsInfinity(xMm) || double.IsNaN(yMm) || double.IsInfinity(yMm))
                return CommandResult.Fail("INVALID_PARAMS: x_mm and y_mm must be finite numbers.");

            if (!Lookup.TrySheet(doc, p.Value<long?>("sheet_id"), p.Value<string>("sheet_number"), out var sheet, out var failure)) return failure;
            if (!Lookup.TrySchedule(doc, p.Value<long?>("schedule_id"), p.Value<string>("schedule_name"), out var schedule, out failure)) return failure;

            var categoryId = schedule.Definition?.CategoryId;
            if (categoryId != null && categoryId == new ElementId(BuiltInCategory.OST_Revisions))
                return CommandResult.Fail("CANNOT_PLACE: a title block revision schedule cannot be placed on a sheet directly.");
            if (categoryId != null && categoryId == new ElementId(BuiltInCategory.OST_KeynoteTags))
                return CommandResult.Fail("CANNOT_PLACE: a keynote legend cannot be placed on a sheet with this command.");

            var point = new XYZ(Mm.ToFeet(xMm), Mm.ToFeet(yMm), 0);
            using (var tx = new Transaction(doc, "Plinth: Place Schedule On Sheet"))
            {
                tx.Start();
                try
                {
                    var instance = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, point);
                    if (instance == null) throw new InvalidOperationException("Revit returned no schedule instance.");
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    var at = instance.Point;
                    var box = instance.get_BoundingBox(sheet);
                    return CommandResult.Ok(new
                    {
                        schedule_instance_id = RevitCompat.GetId(instance.Id),
                        sheet_id = RevitCompat.GetId(sheet.Id),
                        sheet_number = sheet.SheetNumber,
                        schedule_id = RevitCompat.GetId(schedule.Id),
                        schedule_name = schedule.Name,
                        point_sheet_mm = new { x = ViewportSheetSupport.ToMm(at.X), y = ViewportSheetSupport.ToMm(at.Y) },
                        box_sheet_mm = box == null ? null : ViewportSheetSupport.RectDto(new SheetRect(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y))
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("PLACE_FAILED: could not place the schedule: " + ex.Message + " Nothing was created.");
                }
            }
        }
    }
}
