// New in Plinth (2026-11-27, after a review of live plumbing builds): create_mep_run tees a branch into a pipe or duct
// main, but the caller knows one element id per main (its first segment) while the join point usually lies on a later
// segment, and every tee breaks the main so the original id covers less of it each time. The handler therefore walks the
// main's connected network and asks this which straight segment the join point lies beside: its projection must fall at
// least the margin from both ends (a tee needs main each side), and it must be within maxOffsetMm of the segment's line;
// the nearest such segment wins (the first listed on a tie). Revit-free, so it is unit-tested.

using System;
using System.Collections.Generic;

namespace Plinth.Revit.Core.Mep
{
    public static class MainSegments
    {
        /// <summary>
        /// The index of the segment (start, end; mm) the point lies beside, or -1 when none does. A segment takes the point
        /// when its projection is at least marginMm from both ends and the point is at most maxOffsetMm from the line
        /// (double.PositiveInfinity for no limit); among those, the nearest to the point.
        /// </summary>
        public static int Containing(double[] pointMm, IReadOnlyList<double[][]> segmentsMm, double marginMm, double maxOffsetMm)
        {
            var best = -1;
            var bestOffset = double.MaxValue;
            for (var i = 0; i < segmentsMm.Count; i++)
            {
                var offset = Offset(pointMm, segmentsMm[i][0], segmentsMm[i][1], marginMm);
                if (offset == null || offset.Value > maxOffsetMm || offset.Value >= bestOffset) continue;
                best = i;
                bestOffset = offset.Value;
            }
            return best;
        }

        /// <summary>
        /// The point's distance from the segment's line when its projection lies at least marginMm from both ends, else null
        /// (also null for a segment shorter than twice the margin).
        /// </summary>
        public static double? Offset(double[] pointMm, double[] a, double[] b, double marginMm)
        {
            double dx = b[0] - a[0], dy = b[1] - a[1], dz = b[2] - a[2];
            var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (length <= 0 || length < 2 * marginMm) return null;
            double ux = dx / length, uy = dy / length, uz = dz / length;
            double px = pointMm[0] - a[0], py = pointMm[1] - a[1], pz = pointMm[2] - a[2];
            var t = px * ux + py * uy + pz * uz;
            if (t < marginMm || t > length - marginMm) return null;
            double ox = px - t * ux, oy = py - t * uy, oz = pz - t * uz;
            return Math.Sqrt(ox * ox + oy * oy + oz * oz);
        }
    }
}
