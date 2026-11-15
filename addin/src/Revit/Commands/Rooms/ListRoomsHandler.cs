// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Rooms/ListRoomsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: filters run before the boundary calculation (rvt-mcp computed every room's
// boundary first, the slow part); a department filter (B2M flat ID); unknown level or phase names
// are errors; the status counts cover the filtered rooms.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Rooms
{
    public sealed class ListRoomsHandler : IRevitCommand
    {
        public string Name => "list_rooms";
        public string Description => "List rooms with status (placed, unplaced, not_enclosed), area, department and location. Filter by level, phase, department, status.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""level_name"":{""type"":""string""},
            ""phase_name"":{""type"":""string""},
            ""department"":{""type"":""string""},
            ""status"":{""type"":""string"",""enum"":[""all"",""placed"",""unplaced"",""not_enclosed""]},
            ""include_parameters"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var levelName = p.Value<string>("level_name");
            var phaseName = p.Value<string>("phase_name");
            var department = p.Value<string>("department");
            var statusFilter = p.Value<string>("status") ?? "all";
            var includeParameters = p.Value<bool?>("include_parameters") ?? false;
            var limit = p.Value<int?>("limit") ?? 5000;
            if (limit < 1 || limit > 20000) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to 20000.");

            Level level = null;
            if (!string.IsNullOrEmpty(levelName) && !Lookup.TryLevel(doc, levelName, out level, out var failure)) return failure;
            Phase phase = null;
            if (!string.IsNullOrEmpty(phaseName))
            {
                phase = new FilteredElementCollector(doc).OfClass(typeof(Phase)).Cast<Phase>()
                    .FirstOrDefault(ph => string.Equals(ph.Name, phaseName, StringComparison.OrdinalIgnoreCase));
                if (phase == null) return CommandResult.Fail("PHASE_NOT_FOUND: no phase named '" + phaseName + "'.");
            }

            var candidates = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => level == null || r.LevelId == level.Id)
                .Where(r => phase == null || r.CreatedPhaseId == phase.Id)
                .Where(r => string.IsNullOrEmpty(department)
                    || string.Equals(r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString(), department, StringComparison.Ordinal));

            var rooms = new JArray();
            int placed = 0, unplaced = 0, notEnclosed = 0, matching = 0;
            foreach (var r in candidates)
            {
                var status = RoomStatus.Of(r);
                if (status == "placed") placed++;
                else if (status == "unplaced") unplaced++;
                else notEnclosed++;
                if (statusFilter != "all" && status != statusFilter) continue;
                matching++;
                if (rooms.Count >= limit) continue;

                var createdPhase = doc.GetElement(r.CreatedPhaseId) as Phase;
                var point = (r.Location as LocationPoint)?.Point;
                var row = new JObject
                {
                    ["element_id"] = RevitCompat.GetId(r.Id),
                    ["unique_id"] = r.UniqueId,
                    ["name"] = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                    ["number"] = r.Number,
                    ["level"] = r.Level == null ? null : new JObject { ["element_id"] = RevitCompat.GetId(r.Level.Id), ["name"] = r.Level.Name },
                    ["phase"] = createdPhase == null ? null : new JObject { ["element_id"] = RevitCompat.GetId(createdPhase.Id), ["name"] = createdPhase.Name },
                    ["status"] = status,
                    ["location"] = point == null ? null : new JObject { ["x_mm"] = Mm.FromFeet(point.X), ["y_mm"] = Mm.FromFeet(point.Y), ["z_mm"] = Mm.FromFeet(point.Z) },
                    ["area_m2"] = Math.Round(r.Area * 0.09290304, 4),
                    ["perimeter_m"] = Math.Round(r.Perimeter * 0.3048, 4),
                    ["volume_m3"] = Math.Round(r.Volume * 0.0283168466, 4),
                    ["department"] = r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString() ?? "",
                    ["occupancy"] = r.get_Parameter(BuiltInParameter.ROOM_OCCUPANCY)?.AsString() ?? ""
                };
                if (includeParameters) row["parameters"] = Parameters(r);
                rooms.Add(row);
            }

            return CommandResult.Ok(new JObject
            {
                ["total"] = matching,
                ["returned"] = rooms.Count,
                ["counts"] = new JObject { ["placed"] = placed, ["unplaced"] = unplaced, ["not_enclosed"] = notEnclosed },
                ["rooms"] = rooms
            });
        }

        private static JObject Parameters(Element element)
        {
            var values = new JObject();
            foreach (Parameter param in element.Parameters)
            {
                try
                {
                    if (!param.HasValue || string.IsNullOrEmpty(param.Definition?.Name)) continue;
                    switch (param.StorageType)
                    {
                        case StorageType.Double: values[param.Definition.Name] = param.AsDouble(); break;
                        case StorageType.Integer: values[param.Definition.Name] = param.AsInteger(); break;
                        case StorageType.String: values[param.Definition.Name] = param.AsString(); break;
                        case StorageType.ElementId: values[param.Definition.Name] = RevitCompat.GetId(param.AsElementId()); break;
                    }
                }
                catch
                {
                    // Unreadable parameters are skipped.
                }
            }
            return values;
        }
    }
}
