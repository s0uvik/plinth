// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Geometry/FindElementsInVolumeHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: mm through Mm; the volume's min and max points are checked field by field; an
// unknown category is an error listing valid names (rvt-mcp ignored it and searched every category);
// the room itself is left out of its own results; limit must be 1 to 500.

using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Geometry
{
    using View = Autodesk.Revit.DB.View;

    public sealed class FindElementsInVolumeHandler : IRevitCommand
    {
        private const int MaxLimit = 500;

        public string Name => "find_elements_in_volume";
        public string Description =>
            "Find elements whose bounding boxes intersect (default) or lie inside an axis-aligned volume "
            + "{min:{x,y,z}, max:{x,y,z}} in mm, or a room's bounding box (room_id). Optional categories and view; limit 1-500 (default 200).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""volume"":{""type"":""object""},
            ""room_id"":{""type"":""integer""},
            ""categories"":{""type"":""array"",""items"":{""type"":""string""}},
            ""view_id"":{""type"":""integer""},
            ""match"":{""type"":""string"",""enum"":[""inside"",""intersects""]},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var volume = p["volume"] as JObject;
            var roomId = p.Value<long?>("room_id");
            if ((volume == null) == (roomId == null))
                return CommandResult.Fail("INVALID_PARAMS: give exactly one of volume or room_id.");
            var limit = p.Value<int?>("limit") ?? 200;
            if (limit < 1 || limit > MaxLimit) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to " + MaxLimit + ".");
            var match = p.Value<string>("match") ?? "intersects";
            if (!GeometryHelpers.TryOptionalView(doc, p.Value<long?>("view_id"), out View view, out var failure)) return failure;

            XYZ min, max;
            string source;
            var limitations = new JArray();
            Room room = null;
            if (volume != null)
            {
                source = "axis_aligned_volume";
                if (!TryPoint(volume["min"], "volume.min", out min, out failure)) return failure;
                if (!TryPoint(volume["max"], "volume.max", out max, out failure)) return failure;
                if (min.X > max.X || min.Y > max.Y || min.Z > max.Z)
                    return CommandResult.Fail("INVALID_PARAMS: volume.min must not exceed volume.max on any axis.");
            }
            else
            {
                source = "room_bounding_box";
                if (!Lookup.TryElement(doc, roomId.Value, "room", out room, out failure)) return failure;
                var box = room.get_BoundingBox(null);
                if (box == null) return CommandResult.Fail("NO_BOUNDING_BOX: room " + roomId.Value + " has no bounding box (is it placed and enclosed?).");
                min = box.Min;
                max = box.Max;
                limitations.Add("Room mode uses the room's axis-aligned bounding box, not its exact boundary.");
            }

            var outline = new Autodesk.Revit.DB.Outline(min, max);
            var collector = GeometryHelpers.Collector(doc, view).WhereElementIsNotElementType()
                .WherePasses(match == "inside" ? (ElementFilter)new BoundingBoxIsInsideFilter(outline) : new BoundingBoxIntersectsFilter(outline));
            if (p["categories"] is JArray names && names.Count > 0)
            {
                if (!GeometryHelpers.TryCategories(doc, names.Values<string>(), out var categories, out failure)) return failure;
                collector.WherePasses(new ElementMulticategoryFilter(categories.ConvertAll(c => c.Id)));
            }

            var elements = new JArray();
            var matched = 0;
            foreach (var element in collector)
            {
                if (room != null && element.Id == room.Id) continue;
                matched++;
                if (elements.Count >= limit) continue;
                var row = GeometryHelpers.Describe(element);
                row["bounding_box"] = GeometryHelpers.Box(element.get_BoundingBox(view));
                elements.Add(row);
            }

            return CommandResult.Ok(new JObject
            {
                ["unit"] = "mm",
                ["source"] = source,
                ["match"] = match,
                ["volume"] = GeometryHelpers.Box(min, max),
                ["matched"] = matched,
                ["returned"] = elements.Count,
                ["limit"] = limit,
                ["truncated"] = matched > elements.Count,
                ["elements"] = elements,
                ["limitations"] = limitations
            });
        }

        private static bool TryPoint(JToken token, string what, out XYZ point, out CommandResult failure)
        {
            point = null;
            failure = null;
            var o = token as JObject;
            var values = new List<double>();
            foreach (var axis in new[] { "x", "y", "z" })
            {
                var v = o?[axis];
                if (v == null || (v.Type != JTokenType.Integer && v.Type != JTokenType.Float))
                {
                    failure = CommandResult.Fail("INVALID_PARAMS: " + what + " must be {\"x\": mm, \"y\": mm, \"z\": mm}.");
                    return false;
                }
                values.Add(Mm.ToFeet(v.Value<double>()));
            }
            point = new XYZ(values[0], values[1], values[2]);
            return true;
        }
    }
}
