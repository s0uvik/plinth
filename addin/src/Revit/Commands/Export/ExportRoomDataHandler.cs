// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Rooms/ExportRoomDataHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: read-only (rvt-mcp listed it as a write, but it only ever returned data; its unused
// "output" parameter is dropped, use export_elements_data or export_schedule_csv for files); perimeter in mm
// through Mm, area in m2 and volume in m3; unplaced and unenclosed rooms are counted instead of silently
// skipped; paged with start_index / max_results so large projects stay under the response cap.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Export
{
    public sealed class ExportRoomDataHandler : IRevitCommand
    {
        private const double SquareMetresPerSquareFoot = 0.09290304;
        private const double CubicMetresPerCubicFoot = 0.028316846592;

        public string Name => "export_room_data";
        public string Description =>
            "Return data for every placed, enclosed room (name, number, level, area m2, perimeter mm, volume m3, department), "
            + "ordered by level then number, paged with start_index / max_results (default 500, max 2000). Writes no file.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + ExportHelpers.PagingSchema + @"}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!ExportHelpers.TryPaging(p, 500, 2000, out var start, out var max, out var failure)) return failure;

            var all = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<Room>().ToList();
            var unplaced = all.Count(r => r.Location == null);
            var unenclosed = all.Count(r => r.Location != null && r.Area <= 0);
            var rooms = all.Where(r => r.Location != null && r.Area > 0)
                .Select(r => new
                {
                    elementId = RevitCompat.GetId(r.Id),
                    name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? r.Name,
                    number = r.Number,
                    level = r.Level?.Name,
                    levelElevation = r.Level?.ProjectElevation ?? 0,
                    areaM2 = Math.Round(r.Area * SquareMetresPerSquareFoot, 2),
                    perimeterMm = Mm.FromFeet(r.Perimeter),
                    volumeM3 = Math.Round(r.Volume * CubicMetresPerCubicFoot, 3),
                    department = r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString()
                })
                .OrderBy(r => r.levelElevation).ThenBy(r => r.level).ThenBy(r => r.number, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var page = rooms.Skip(start).Take(max).ToList();
            return CommandResult.Ok(new
            {
                projectName = doc.Title,
                totalRooms = rooms.Count,
                totalAreaM2 = Math.Round(rooms.Sum(r => r.areaM2), 2),
                unplacedRooms = unplaced,
                unenclosedRooms = unenclosed,
                start_index = start,
                returned = page.Count,
                truncated = start + page.Count < rooms.Count,
                next_index = start + page.Count < rooms.Count ? (int?)(start + page.Count) : null,
                rooms = page.Select(r => new { r.elementId, r.name, r.number, r.level, r.areaM2, r.perimeterMm, r.volumeM3, r.department })
            });
        }
    }
}
