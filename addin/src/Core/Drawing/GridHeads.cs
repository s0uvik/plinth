// New in B2M: the Revit-free half of set_grid_heads. A grid's line in a plan view is cut (or stretched) to a box around
// the building, so its heads sit just outside the building at both ends whatever length the grid was modelled with.

using System;

namespace B2M.Revit.Core.Drawing
{
    /// <summary>A 2D segment from (X0, Y0) to (X1, Y1).</summary>
    public sealed class Segment2
    {
        public Segment2(double x0, double y0, double x1, double y1)
        {
            X0 = x0;
            Y0 = y0;
            X1 = x1;
            Y1 = y1;
        }

        public double X0 { get; }
        public double Y0 { get; }
        public double X1 { get; }
        public double Y1 { get; }
    }

    public static class GridHeads
    {
        /// <summary>
        /// The part of the infinite line through (x0, y0) -> (x1, y1) inside the box, in the same direction as the given
        /// segment (so a grid keeps its start and end, and the bubble Revit shows at each). Null when the line misses the
        /// box or the segment has no length.
        /// </summary>
        public static Segment2 ClipToBox(double x0, double y0, double x1, double y1,
                                         double minX, double minY, double maxX, double maxY)
        {
            double dx = x1 - x0, dy = y1 - y0;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-9) return null;
            dx /= length;
            dy /= length;
            double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
            if (!Slab(x0, dx, minX, maxX, ref lo, ref hi) || !Slab(y0, dy, minY, maxY, ref lo, ref hi)) return null;
            if (hi - lo < 1e-9) return null;
            return new Segment2(x0 + dx * lo, y0 + dy * lo, x0 + dx * hi, y0 + dy * hi);
        }

        // Narrows [lo, hi] (distances along the line) to where the coordinate is within [min, max].
        private static bool Slab(double origin, double direction, double min, double max, ref double lo, ref double hi)
        {
            if (Math.Abs(direction) < 1e-12) return origin >= min && origin <= max;
            var t0 = (min - origin) / direction;
            var t1 = (max - origin) / direction;
            if (t0 > t1)
            {
                var swap = t0;
                t0 = t1;
                t1 = swap;
            }
            lo = Math.Max(lo, t0);
            hi = Math.Min(hi, t1);
            return lo <= hi;
        }
    }
}
