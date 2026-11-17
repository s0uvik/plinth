using System.Collections.Generic;
using Autodesk.Revit.DB;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands
{
    /// <summary>Turns a JSON list of {x, y} points in mm into a closed, horizontal outline.</summary>
    internal static class Outline
    {
        public static bool TryPoints(JArray points, double z, double shortCurve, out List<XYZ> result, out CommandResult failure)
        {
            result = new List<XYZ>();
            failure = null;
            if (points == null || points.Count < 3)
            {
                failure = CommandResult.Fail("INVALID_OUTLINE: give at least 3 points, each {\"x\": mm, \"y\": mm}.");
                return false;
            }
            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i] as JObject;
                var x = point?["x"];
                var y = point?["y"];
                if (x == null || y == null || (x.Type != JTokenType.Integer && x.Type != JTokenType.Float)
                    || (y.Type != JTokenType.Integer && y.Type != JTokenType.Float))
                {
                    failure = CommandResult.Fail("INVALID_OUTLINE: point " + i + " must be {\"x\": mm, \"y\": mm}.");
                    return false;
                }
                result.Add(new XYZ(Mm.ToFeet(x.Value<double>()), Mm.ToFeet(y.Value<double>()), z));
            }
            for (var i = 0; i < result.Count; i++)
            {
                if (result[i].DistanceTo(result[(i + 1) % result.Count]) <= shortCurve)
                {
                    failure = CommandResult.Fail("INVALID_OUTLINE: points " + i + " and " + ((i + 1) % result.Count)
                        + " are too close; remove repeated points (do not repeat the first point at the end).");
                    return false;
                }
            }
            return true;
        }

        public static bool TryLoop(JArray points, double z, double shortCurve, out CurveLoop loop, out CommandResult failure)
        {
            loop = null;
            if (!TryPoints(points, z, shortCurve, out var xyz, out failure)) return false;
            loop = new CurveLoop();
            for (var i = 0; i < xyz.Count; i++) loop.Append(Line.CreateBound(xyz[i], xyz[(i + 1) % xyz.Count]));
            return true;
        }

        public static bool TryCurveArray(JArray points, double z, double shortCurve, out CurveArray curves, out CommandResult failure)
        {
            curves = null;
            if (!TryPoints(points, z, shortCurve, out var xyz, out failure)) return false;
            curves = new CurveArray();
            for (var i = 0; i < xyz.Count; i++) curves.Append(Line.CreateBound(xyz[i], xyz[(i + 1) % xyz.Count]));
            return true;
        }
    }
}
