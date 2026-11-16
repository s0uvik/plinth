// New in B2M (Step 10, F5): create_mep_run draws a pipe, duct, conduit or cable tray along a polyline of 3D points and
// puts an elbow at every bend. This is the Revit-free part: checking the points, dropping points that lie on a straight
// line (no fitting is needed there, so the two pieces become one segment) and listing each bend with its angle.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace B2M.Revit.Core.Mep
{
    public static class RunPath
    {
        /// <summary>Bends under this many degrees are straight: the point is dropped and no fitting is made.</summary>
        public const double StraightDeg = 0.5;

        /// <summary>A run may not turn back on itself; bends over this many degrees are refused.</summary>
        public const double MaxBendDeg = 179.0;

        public sealed class Bend
        {
            /// <summary>Index of the bend point in Plan.Points (1 .. Count-2): the joint between segment Index-1 and segment Index.</summary>
            public int Index;

            /// <summary>Index of the same point in the caller's list.</summary>
            public int SourceIndex;

            /// <summary>Deflection from straight on, in degrees (90 for a square turn).</summary>
            public double AngleDeg;
        }

        public sealed class Plan
        {
            /// <summary>The points kept, in mm.</summary>
            public List<double[]> Points = new List<double[]>();

            /// <summary>For each kept point, its index in the caller's list.</summary>
            public List<int> SourceIndices = new List<int>();

            public List<Bend> Bends = new List<Bend>();

            /// <summary>Caller's indices of points dropped because they lay on a straight line.</summary>
            public List<int> Dropped = new List<int>();

            /// <summary>"INVALID_PARAMS: ..." when the points cannot make a run; null otherwise.</summary>
            public string Error;

            public int SegmentCount => Points.Count - 1;
        }

        /// <summary>
        /// Checks a polyline of [x, y, z] points in mm. Each segment must be longer than minSegmentMm (Revit's shortest
        /// curve); points on a straight line are dropped; a bend sharper than MaxBendDeg is refused.
        /// </summary>
        public static Plan Build(IList<double[]> points, double minSegmentMm, string field = "points")
        {
            var plan = new Plan();
            if (points == null || points.Count < 2)
            {
                plan.Error = "INVALID_PARAMS: " + field + " needs at least 2 points, each [x, y, z] in mm.";
                return plan;
            }
            for (var i = 0; i < points.Count; i++)
            {
                var p = points[i];
                if (p == null || p.Length != 3 || !Finite(p[0]) || !Finite(p[1]) || !Finite(p[2]))
                {
                    plan.Error = "INVALID_PARAMS: " + field + "[" + i + "] must be [x, y, z], finite numbers of mm.";
                    return plan;
                }
                if (i > 0 && Distance(points[i - 1], p) <= minSegmentMm)
                {
                    plan.Error = "INVALID_PARAMS: " + field + "[" + (i - 1) + "] and " + field + "[" + i + "] are " + Format(Distance(points[i - 1], p))
                        + " mm apart, not more than Revit's shortest segment (" + Format(minSegmentMm) + " mm); remove repeated points.";
                    return plan;
                }
            }

            plan.Points.Add(points[0]);
            plan.SourceIndices.Add(0);
            for (var i = 1; i < points.Count - 1; i++)
            {
                var angle = DeflectionDeg(plan.Points[plan.Points.Count - 1], points[i], points[i + 1]);
                if (angle < StraightDeg)
                {
                    plan.Dropped.Add(i);
                    continue;
                }
                if (angle > MaxBendDeg)
                {
                    plan.Error = "INVALID_PARAMS: the run turns back on itself at " + field + "[" + i + "] (" + Format(angle)
                        + " degrees); a run cannot double back.";
                    return plan;
                }
                plan.Points.Add(points[i]);
                plan.SourceIndices.Add(i);
            }
            plan.Points.Add(points[points.Count - 1]);
            plan.SourceIndices.Add(points.Count - 1);

            for (var k = 1; k < plan.Points.Count - 1; k++)
                plan.Bends.Add(new Bend
                {
                    Index = k,
                    SourceIndex = plan.SourceIndices[k],
                    AngleDeg = Math.Round(DeflectionDeg(plan.Points[k - 1], plan.Points[k], plan.Points[k + 1]), 3)
                });
            return plan;
        }

        /// <summary>Angle in degrees between the direction a→b and the direction b→c (0 when straight on).</summary>
        public static double DeflectionDeg(double[] a, double[] b, double[] c)
        {
            var u = Unit(b[0] - a[0], b[1] - a[1], b[2] - a[2]);
            var v = Unit(c[0] - b[0], c[1] - b[1], c[2] - b[2]);
            var dot = Math.Max(-1.0, Math.Min(1.0, u[0] * v[0] + u[1] * v[1] + u[2] * v[2]));
            return Math.Acos(dot) * 180.0 / Math.PI;
        }

        public static double Distance(double[] a, double[] b)
        {
            var dx = b[0] - a[0];
            var dy = b[1] - a[1];
            var dz = b[2] - a[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static double[] Unit(double x, double y, double z)
        {
            var length = Math.Sqrt(x * x + y * y + z * z);
            return new[] { x / length, y / length, z / length };
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static string Format(double value) => Math.Round(value, 3).ToString(CultureInfo.InvariantCulture);
    }
}
