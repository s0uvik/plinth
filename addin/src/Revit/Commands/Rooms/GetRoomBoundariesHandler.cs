// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Rooms/GetRoomBoundariesHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: shared status helper; boundary elements carry builtInCategory; otherwise as rvt-mcp.

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
    public sealed class GetRoomBoundariesHandler : IRevitCommand
    {
        private static readonly Dictionary<string, SpatialElementBoundaryLocation> Locations = new Dictionary<string, SpatialElementBoundaryLocation>
        {
            ["finish"] = SpatialElementBoundaryLocation.Finish,
            ["center"] = SpatialElementBoundaryLocation.Center,
            ["core_center"] = SpatialElementBoundaryLocation.CoreCenter,
            ["core_boundary"] = SpatialElementBoundaryLocation.CoreBoundary,
        };

        public string Name => "get_room_boundaries";
        public string Description => "Read a room's boundary loops (points in mm) and the elements that bound each segment.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""room_id"":{""type"":""integer""},
            ""boundary_location"":{""type"":""string"",""enum"":[""finish"",""center"",""core_center"",""core_boundary""]},
            ""include_boundary_elements"":{""type"":""boolean""}},""required"":[""room_id""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var roomId = p.Value<long>("room_id");
            if (!Lookup.TryElement<Room>(doc, roomId, "room", out var room, out var failure)) return failure;
            var locationKey = p.Value<string>("boundary_location") ?? "finish";
            var includeElements = p.Value<bool?>("include_boundary_elements") ?? true;

            var status = RoomStatus.Of(room);
            var summary = new JObject { ["element_id"] = roomId, ["name"] = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(), ["number"] = room.Number, ["status"] = status };
            var loops = new JArray();
            var warnings = new JArray();
            if (status != "placed")
            {
                warnings.Add("Room is " + status + "; it has no boundary.");
                return CommandResult.Ok(Result(summary, locationKey, loops, warnings));
            }

            var segmentsByLoop = room.GetBoundarySegments(new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = Locations[locationKey] });
            if (segmentsByLoop == null)
            {
                warnings.Add("Revit returned no boundary segments.");
                return CommandResult.Ok(Result(summary, locationKey, loops, warnings));
            }

            var loopIndex = 0;
            foreach (var loop in segmentsByLoop)
            {
                var segments = new JArray();
                double loopLength = 0;
                var segmentIndex = 0;
                foreach (var segment in loop)
                {
                    var curve = segment.GetCurve();
                    if (curve == null) continue;
                    var length = Math.Round(curve.Length * 0.3048, 4);
                    loopLength += length;
                    var row = new JObject
                    {
                        ["segment_index"] = segmentIndex++,
                        ["curve_type"] = curve.GetType().Name,
                        ["start"] = Point(curve.GetEndPoint(0)),
                        ["end"] = Point(curve.GetEndPoint(1)),
                        ["length_m"] = length
                    };
                    if (!(curve is Line)) row["tessellated_points"] = new JArray(curve.Tessellate().Select(Point));
                    if (includeElements && segment.ElementId != ElementId.InvalidElementId)
                    {
                        var bounding = doc.GetElement(segment.ElementId);
                        row["element_id"] = RevitCompat.GetId(segment.ElementId);
                        row["element_category"] = bounding?.Category?.Name;
                        row["element_builtInCategory"] = Lookup.BuiltInName(bounding?.Category);
                        row["element_name"] = bounding?.Name;
                        if (segment.LinkElementId != ElementId.InvalidElementId) row["link_element_id"] = RevitCompat.GetId(segment.LinkElementId);
                    }
                    segments.Add(row);
                }
                loops.Add(new JObject { ["loop_index"] = loopIndex++, ["closed"] = true, ["length_m"] = Math.Round(loopLength, 4), ["segments"] = segments });
            }
            return CommandResult.Ok(Result(summary, locationKey, loops, warnings));
        }

        private static JObject Result(JObject room, string location, JArray loops, JArray warnings) => new JObject
        {
            ["room"] = room,
            ["boundary_location"] = location,
            ["loop_count"] = loops.Count,
            ["loops"] = loops,
            ["warnings"] = warnings
        };

        private static JObject Point(XYZ p) => new JObject { ["x_mm"] = Mm.FromFeet(p.X), ["y_mm"] = Mm.FromFeet(p.Y), ["z_mm"] = Mm.FromFeet(p.Z) };
    }
}
