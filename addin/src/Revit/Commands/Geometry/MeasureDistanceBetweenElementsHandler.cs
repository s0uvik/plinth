// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Geometry/MeasureDistanceBetweenElementsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: mm through Mm; misses are ELEMENT_NOT_FOUND errors; curve-to-curve distance
// uses Curve.ComputeClosestPoints when Revit offers it (rvt-mcp only projected end points), falling
// back to the end-point projections.

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Geometry
{
    public sealed class MeasureDistanceBetweenElementsHandler : IRevitCommand
    {
        public string Name => "measure_distance_between_elements";
        public string Description =>
            "Shortest distance in mm between two elements: 'bbox' (axis-aligned boxes, default), 'location' (location points or curves), "
            + "or 'solid_bbox_prefilter' (box distance, flagged as a clash pre-filter only).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_id_1"":{""type"":""integer""},
            ""element_id_2"":{""type"":""integer""},
            ""strategy"":{""type"":""string"",""enum"":[""bbox"",""location"",""solid_bbox_prefilter""]}},
            ""required"":[""element_id_1"",""element_id_2""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!Lookup.TryElement(doc, p.Value<long>("element_id_1"), "model element", out Element e1, out var failure)) return failure;
            if (!Lookup.TryElement(doc, p.Value<long>("element_id_2"), "model element", out Element e2, out failure)) return failure;
            var strategy = p.Value<string>("strategy") ?? "bbox";

            var box1 = e1.get_BoundingBox(null);
            var box2 = e2.get_BoundingBox(null);
            XYZ a, b;
            string limitation;
            if (strategy == "location")
            {
                if (!TryLocationDistance(e1, e2, out a, out b, out var error))
                    return CommandResult.Fail("NO_LOCATION: " + error);
                limitation = "Distance between location points or the closest points of location curves.";
            }
            else
            {
                if (box1 == null || box2 == null)
                    return CommandResult.Fail("NO_BOUNDING_BOX: element " + RevitCompat.GetId((box1 == null ? e1 : e2).Id) + " has no bounding box; try strategy 'location'.");
                BoxDistance(box1, box2, out a, out b);
                limitation = strategy == "bbox"
                    ? "Axis-aligned box distance, not exact solid-to-solid clearance."
                    : "Overlap here means the bounding boxes overlap; no solid intersection was run.";
            }
            var distance = a.DistanceTo(b);

            return CommandResult.Ok(new JObject
            {
                ["unit"] = "mm",
                ["element_id_1"] = RevitCompat.GetId(e1.Id),
                ["element_id_2"] = RevitCompat.GetId(e2.Id),
                ["strategy"] = strategy,
                ["distance"] = Mm.FromFeet(distance),
                ["intersects"] = distance <= 1e-7,
                ["point_1"] = GeometryHelpers.Point(a),
                ["point_2"] = GeometryHelpers.Point(b),
                ["bbox_1"] = GeometryHelpers.Box(box1),
                ["bbox_2"] = GeometryHelpers.Box(box2),
                ["limitations"] = new JArray(limitation)
            });
        }

        private static void BoxDistance(BoundingBoxXYZ a, BoundingBoxXYZ b, out XYZ pa, out XYZ pb)
        {
            Axis(a.Min.X, a.Max.X, b.Min.X, b.Max.X, out var ax, out var bx);
            Axis(a.Min.Y, a.Max.Y, b.Min.Y, b.Max.Y, out var ay, out var by);
            Axis(a.Min.Z, a.Max.Z, b.Min.Z, b.Max.Z, out var az, out var bz);
            pa = new XYZ(ax, ay, az);
            pb = new XYZ(bx, by, bz);
        }

        private static void Axis(double aMin, double aMax, double bMin, double bMax, out double a, out double b)
        {
            if (aMax < bMin) { a = aMax; b = bMin; }
            else if (bMax < aMin) { a = aMin; b = bMax; }
            else a = b = 0.5 * (Math.Max(aMin, bMin) + Math.Min(aMax, bMax));
        }

        private static bool TryLocationDistance(Element e1, Element e2, out XYZ a, out XYZ b, out string error)
        {
            a = b = null;
            error = null;
            var p1 = (e1.Location as LocationPoint)?.Point;
            var c1 = (e1.Location as LocationCurve)?.Curve;
            var p2 = (e2.Location as LocationPoint)?.Point;
            var c2 = (e2.Location as LocationCurve)?.Curve;
            if ((p1 == null && c1 == null) || (p2 == null && c2 == null))
            {
                error = "element " + RevitCompat.GetId((p1 == null && c1 == null ? e1 : e2).Id) + " has no location point or curve; use strategy 'bbox'.";
                return false;
            }
            if (p1 != null && p2 != null) { a = p1; b = p2; return true; }
            if (p1 != null) { a = p1; b = Closest(c2, p1); return true; }
            if (p2 != null) { b = p2; a = Closest(c1, p2); return true; }

            var best = double.MaxValue;
            void Consider(XYZ x, XYZ y, ref XYZ ra, ref XYZ rb)
            {
                var d = x.DistanceTo(y);
                if (d < best) { best = d; ra = x; rb = y; }
            }
            try
            {
                c1.ComputeClosestPoints(c2, true, true, false, out IList<ClosestPointsPairBetweenTwoCurves> pairs);
                if (pairs != null)
                    foreach (var pair in pairs) Consider(pair.XYZPointOnFirstCurve, pair.XYZPointOnSecondCurve, ref a, ref b);
            }
            catch
            {
                // Older or unusual curve types: the end-point projections below still give an answer.
            }
            for (var i = 0; i < 2; i++)
            {
                var e = c1.GetEndPoint(i);
                Consider(e, Closest(c2, e), ref a, ref b);
                var f = c2.GetEndPoint(i);
                Consider(Closest(c1, f), f, ref a, ref b);
            }
            return true;
        }

        private static XYZ Closest(Curve curve, XYZ point)
        {
            var projected = curve.Project(point)?.XYZPoint;
            if (projected != null) return projected;
            var s = curve.GetEndPoint(0);
            var e = curve.GetEndPoint(1);
            return point.DistanceTo(s) <= point.DistanceTo(e) ? s : e;
        }
    }
}
