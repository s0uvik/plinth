// New in Plinth: Revit-free checks on a closed plan outline of [x, y] points in mm (create_foundation_slab), so a bad
// outline is refused with a clear message before Revit is asked (Revit would throw or warn, and a warning rolls back).

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Plinth.Revit.Core.Geometry
{
    public static class PlanPolygon
    {
        /// <summary>Below this area (mm²) an outline has no extent.</summary>
        public const double MinAreaMm2 = 1.0;

        /// <summary>Signed area in mm² (positive counter-clockwise); the outline is closed implicitly (last point to first).</summary>
        public static double SignedAreaMm2(IList<double[]> points)
        {
            if (points == null || points.Count < 3) return 0;
            var sum = 0.0;
            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                sum += a[0] * b[1] - b[0] * a[1];
            }
            return sum / 2;
        }

        public static double AreaMm2(IList<double[]> points) => Math.Abs(SignedAreaMm2(points));

        /// <summary>
        /// Null when the outline is a simple polygon: 3 or more points, the first not repeated at the end, no two edges
        /// crossing or touching except neighbours at their shared point, and an area of at least MinAreaMm2.
        /// Otherwise "INVALID_PARAMS: ..." naming the problem.
        /// </summary>
        public static string Validate(IList<double[]> points, string field)
        {
            if (points == null || points.Count < 3) return "INVALID_PARAMS: " + field + " needs at least 3 points, each [x, y] in mm.";
            var n = points.Count;
            for (var i = 0; i < n; i++)
                if (points[i] == null || points[i].Length != 2 || !Finite(points[i][0]) || !Finite(points[i][1]))
                    return "INVALID_PARAMS: " + field + "[" + i + "] must be [x, y], finite numbers of mm.";
            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; j < n; j++)
                {
                    if (j == i + 1 || (i == 0 && j == n - 1))
                    {
                        // Neighbouring edges share one point; they must not fold back over each other.
                        if (Collinear(points, i, j) && Overlap(points, i, j))
                            return "INVALID_PARAMS: " + field + " edges " + i + " and " + j + " fold back over each other; remove the spike point.";
                        continue;
                    }
                    if (Intersect(points[i], points[(i + 1) % n], points[j], points[(j + 1) % n]))
                        return "INVALID_PARAMS: " + field + " edges " + i + " and " + j + " cross or touch (edge k runs from point k to point k+1); the "
                            + "outline must be one simple closed polygon (no figure of eight, no repeated first point at the end).";
                }
            }
            var area = AreaMm2(points);
            if (area < MinAreaMm2)
                return "INVALID_PARAMS: " + field + " encloses no area (" + area.ToString("0.###", CultureInfo.InvariantCulture) + " mm²); its points lie on one line.";
            return null;
        }

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        private static double Cross(double[] o, double[] a, double[] b) => (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);

        private static bool OnSegment(double[] p, double[] q, double[] r) =>
            Math.Min(p[0], r[0]) - 1e-9 <= q[0] && q[0] <= Math.Max(p[0], r[0]) + 1e-9
            && Math.Min(p[1], r[1]) - 1e-9 <= q[1] && q[1] <= Math.Max(p[1], r[1]) + 1e-9;

        /// <summary>Segments ab and cd share at least one point.</summary>
        private static bool Intersect(double[] a, double[] b, double[] c, double[] d)
        {
            var d1 = Cross(c, d, a);
            var d2 = Cross(c, d, b);
            var d3 = Cross(a, b, c);
            var d4 = Cross(a, b, d);
            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
            const double eps = 1e-9;
            return (Math.Abs(d1) <= eps && OnSegment(c, a, d)) || (Math.Abs(d2) <= eps && OnSegment(c, b, d))
                || (Math.Abs(d3) <= eps && OnSegment(a, c, b)) || (Math.Abs(d4) <= eps && OnSegment(a, d, b));
        }

        /// <summary>Edges i and j are neighbours; true when all three of their points lie on one line.</summary>
        private static bool Collinear(IList<double[]> points, int i, int j)
        {
            var n = points.Count;
            var a = points[i];
            var b = points[(i + 1) % n];
            var c = points[j];
            var d = points[(j + 1) % n];
            var length = Math.Max(Distance(a, b), Distance(c, d));
            return length > 0 && Math.Abs(Cross(a, b, c)) / length <= 1e-6 && Math.Abs(Cross(a, b, d)) / length <= 1e-6;
        }

        /// <summary>Collinear neighbours overlap when their directions away from the shared point agree (a spike).</summary>
        private static bool Overlap(IList<double[]> points, int i, int j)
        {
            var n = points.Count;
            // The shared point and the two far ends.
            double[] shared, p, q;
            if (j == i + 1)
            {
                shared = points[j];
                p = points[i];
                q = points[(j + 1) % n];
            }
            else
            {
                shared = points[0];
                p = points[1];
                q = points[n - 1];
            }
            var dot = (p[0] - shared[0]) * (q[0] - shared[0]) + (p[1] - shared[1]) * (q[1] - shared[1]);
            return dot > 0;
        }

        private static double Distance(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]));
    }
}
