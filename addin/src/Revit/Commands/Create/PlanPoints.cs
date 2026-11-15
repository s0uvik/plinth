// New in B2M: shared parsing for the snake_case create commands (create_stair, create_railing, create_roof),
// which take plan points as [x, y] arrays in mm rather than the {x, y} objects of the ported commands.

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Create
{
    internal static class PlanPoints
    {
        /// <summary>One [x, y] point in mm at height z (feet).</summary>
        public static bool TryPoint(JToken token, string label, double z, out XYZ point, out CommandResult failure)
        {
            point = null;
            failure = null;
            var pair = token as JArray;
            if (pair == null || pair.Count != 2 || !IsNumber(pair[0]) || !IsNumber(pair[1]))
            {
                failure = CommandResult.Fail("INVALID_PARAMS: " + label + " must be [x, y] in mm.");
                return false;
            }
            var x = pair[0].Value<double>();
            var y = pair[1].Value<double>();
            if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y))
            {
                failure = CommandResult.Fail("INVALID_PARAMS: " + label + " must be finite numbers.");
                return false;
            }
            point = new XYZ(Mm.ToFeet(x), Mm.ToFeet(y), z);
            return true;
        }

        /// <summary>
        /// A list of [x, y] points in mm at height z. Consecutive points (and, when closed, the last and first)
        /// must be further apart than Revit's shortest curve. A closed list must not repeat its first point.
        /// </summary>
        public static bool TryList(JToken token, string field, int minCount, bool closed, double z, double shortCurve,
            out List<XYZ> points, out CommandResult failure)
        {
            points = new List<XYZ>();
            failure = null;
            var array = token as JArray;
            if (array == null || array.Count < minCount)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: " + field + " needs at least " + minCount + " points, each [x, y] in mm.");
                return false;
            }
            for (var i = 0; i < array.Count; i++)
            {
                if (!TryPoint(array[i], field + "[" + i + "]", z, out var point, out failure)) return false;
                points.Add(point);
            }
            var edges = closed ? points.Count : points.Count - 1;
            for (var i = 0; i < edges; i++)
            {
                var j = (i + 1) % points.Count;
                if (points[i].DistanceTo(points[j]) <= shortCurve)
                {
                    failure = CommandResult.Fail("INVALID_PARAMS: " + field + " points " + i + " and " + j + " are closer than Revit's shortest "
                        + "allowed line (" + Mm.FromFeet(shortCurve) + " mm); remove repeated points"
                        + (closed ? " (do not repeat the first point at the end)." : "."));
                    return false;
                }
            }
            return true;
        }

        public static JArray ToJson(XYZ point) => new JArray(Mm.FromFeet(point.X), Mm.FromFeet(point.Y));

        public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static bool IsNumber(JToken token) => token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float);

        /// <summary>Same plan position, ignoring height.</summary>
        public static bool SamePlanPoint(XYZ a, XYZ b, double tolerance) =>
            Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance;
    }
}
