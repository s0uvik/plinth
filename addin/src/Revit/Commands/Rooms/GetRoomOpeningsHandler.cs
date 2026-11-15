// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Rooms/GetRoomOpeningsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: each opening says how it was matched ("association": from_room, to_room, room, or
// bounding_box for the window bounding-box fallback, which rvt-mcp applied silently); categories are
// compared by BuiltInCategory; instance width/height are read when the type has none; mm through Mm;
// at most 2000 openings returned, with truncation reported; status via the shared classification.

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
    public sealed class GetRoomOpeningsHandler : IRevitCommand
    {
        private const int MaxOpenings = 2000;

        public string Name => "get_room_openings";
        public string Description =>
            "List the doors and windows of a room (by the room's phase: door from/to room, window room, else a window whose "
            + "bounding box touches the room's, marked association=bounding_box). Sizes and locations in mm.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""room_id"":{""type"":""integer""},
            ""include_doors"":{""type"":""boolean""},
            ""include_windows"":{""type"":""boolean""}},""required"":[""room_id""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var roomId = p.Value<long>("room_id");
            var includeDoors = p.Value<bool?>("include_doors") ?? true;
            var includeWindows = p.Value<bool?>("include_windows") ?? true;
            if (!includeDoors && !includeWindows)
                return CommandResult.Fail("INVALID_PARAMS: include_doors and include_windows cannot both be false.");
            if (!Lookup.TryElement<Room>(doc, roomId, "room", out var room, out var failure)) return failure;

            var roomRef = new JObject { ["element_id"] = roomId, ["name"] = room.Name, ["number"] = room.Number };
            var status = RoomStatus.Of(room);
            if (status == "unplaced")
            {
                return CommandResult.Ok(new JObject
                {
                    ["room"] = roomRef,
                    ["status"] = status,
                    ["openings"] = new JArray(),
                    ["counts"] = new JObject { ["doors"] = 0, ["windows"] = 0 },
                    ["truncated"] = false,
                    ["warnings"] = new JArray("The room is unplaced, so it has no openings.")
                });
            }

            var phase = doc.GetElement(room.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId() ?? ElementId.InvalidElementId) as Phase;
            if (phase == null)
                return CommandResult.Fail("NO_ROOM_PHASE: room " + roomId + " has no phase, so its openings cannot be resolved.");

            var categories = new List<BuiltInCategory>();
            if (includeDoors) categories.Add(BuiltInCategory.OST_Doors);
            if (includeWindows) categories.Add(BuiltInCategory.OST_Windows);
            var instances = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(categories))
                .WhereElementIsNotElementType().OfType<FamilyInstance>();

            var roomBox = room.get_BoundingBox(null);
            var openings = new JArray();
            int doors = 0, windows = 0;
            foreach (var fi in instances)
            {
                var bic = fi.Category?.BuiltInCategory;
                var isDoor = bic == BuiltInCategory.OST_Doors;
                var isWindow = bic == BuiltInCategory.OST_Windows;
                if (!isDoor && !isWindow) continue;

                var fromRoom = Safe(() => fi.get_FromRoom(phase));
                var toRoom = Safe(() => fi.get_ToRoom(phase));
                string association = null;
                if (fromRoom != null && fromRoom.Id == room.Id) association = "from_room";
                else if (toRoom != null && toRoom.Id == room.Id) association = "to_room";
                else if (isWindow)
                {
                    var basic = Safe(() => fi.get_Room(phase));
                    if (basic != null && basic.Id == room.Id) association = "room";
                    else if (roomBox != null && Touches(fi.get_BoundingBox(null), roomBox)) association = "bounding_box";
                }
                if (association == null) continue;

                if (isDoor) doors++;
                else windows++;
                if (openings.Count >= MaxOpenings) continue;

                var symbol = fi.Symbol;
                openings.Add(new JObject
                {
                    ["element_id"] = RevitCompat.GetId(fi.Id),
                    ["unique_id"] = fi.UniqueId,
                    ["category"] = isDoor ? "OST_Doors" : "OST_Windows",
                    ["family"] = symbol?.Family?.Name ?? "",
                    ["type"] = symbol?.Name ?? "",
                    ["mark"] = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "",
                    ["association"] = association,
                    ["from_room_id"] = fromRoom == null ? null : new JValue(RevitCompat.GetId(fromRoom.Id)),
                    ["to_room_id"] = toRoom == null ? null : new JValue(RevitCompat.GetId(toRoom.Id)),
                    ["host_id"] = fi.Host == null ? null : new JValue(RevitCompat.GetId(fi.Host.Id)),
                    ["location"] = RoomsHelpers.Point((fi.Location as LocationPoint)?.Point),
                    ["width_mm"] = Size(fi, symbol, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.GENERIC_WIDTH),
                    ["height_mm"] = Size(fi, symbol, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.GENERIC_HEIGHT)
                });
            }

            return CommandResult.Ok(new JObject
            {
                ["room"] = roomRef,
                ["status"] = status,
                ["phase"] = RoomsHelpers.Ref(phase),
                ["counts"] = new JObject { ["doors"] = doors, ["windows"] = windows },
                ["returned"] = openings.Count,
                ["truncated"] = doors + windows > openings.Count,
                ["openings"] = openings,
                ["warnings"] = new JArray()
            });
        }

        private static Room Safe(Func<Room> read)
        {
            try
            {
                return read();
            }
            catch
            {
                return null; // Revit throws for instances that do not exist in the phase.
            }
        }

        private static bool Touches(BoundingBoxXYZ a, BoundingBoxXYZ b) =>
            a != null
            && a.Min.X <= b.Max.X && a.Max.X >= b.Min.X
            && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y
            && a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;

        /// <summary>The first set length parameter, on the type first, then on the instance; null when none.</summary>
        private static JToken Size(FamilyInstance fi, FamilySymbol symbol, params BuiltInParameter[] ids)
        {
            foreach (var owner in new Element[] { symbol, fi })
            {
                if (owner == null) continue;
                foreach (var id in ids)
                {
                    var param = owner.get_Parameter(id);
                    if (param != null && param.HasValue && param.StorageType == StorageType.Double)
                        return new JValue(Mm.FromFeet(param.AsDouble()));
                }
            }
            return JValue.CreateNull();
        }
    }
}
