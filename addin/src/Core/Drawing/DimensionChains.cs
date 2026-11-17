// New in Plinth: the Revit-free half of the drawing-set annotation commands (auto_dimension_plan,
// auto_dimension_section, spot_level). Positions are plain numbers along one axis in any unit (the Revit side uses
// millimetres); nothing here knows about references, it carries an opaque Key the caller maps back to one.
//   ChainBuilder  - sorts the candidate stops of one dimension chain, merges stops that coincide (a zero-length
//                   segment) keeping the one with the best priority, and refuses chains that are too short;
//   Skyline       - which parts of parallel faces are seen from one side (the exterior wall faces of a plan side);
//   Raycast       - the nearest face in one direction from a point (the clear width of a room);
//   SideMath      - plan sides, which axis they measure and where the n-th chain line goes.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Plinth.Revit.Core.Drawing
{
    /// <summary>One possible stop of a dimension chain.</summary>
    public sealed class ChainStop
    {
        public ChainStop(double position, int priority, string source, int key)
        {
            Position = position;
            Priority = priority;
            Source = source;
            Key = key;
        }

        /// <summary>Coordinate along the chain's measuring axis.</summary>
        public double Position { get; }

        /// <summary>Lower wins when stops coincide (a wall corner beats an opening edge on the same line).</summary>
        public int Priority { get; }

        /// <summary>Human-readable origin for reports, e.g. "wall 123 face" or "door 456 left".</summary>
        public string Source { get; }

        /// <summary>The caller's handle to the reference behind this stop.</summary>
        public int Key { get; }
    }

    /// <summary>A stop that was dropped because it coincided with a kept one.</summary>
    public sealed class MergedStop
    {
        public MergedStop(ChainStop dropped, ChainStop keptInstead)
        {
            Dropped = dropped;
            KeptInstead = keptInstead;
        }

        public ChainStop Dropped { get; }
        public ChainStop KeptInstead { get; }
    }

    public sealed class ChainBuildResult
    {
        public List<ChainStop> Stops { get; } = new List<ChainStop>();
        public List<MergedStop> Merged { get; } = new List<MergedStop>();

        /// <summary>Null when the chain can be created; otherwise why not.</summary>
        public string SkipReason { get; set; }

        public IReadOnlyList<double> Positions => Stops.Select(s => s.Position).ToList();

        public IReadOnlyList<double> Segments => ChainBuilder.Segments(Positions);
    }

    public static class ChainBuilder
    {
        /// <summary>
        /// Sorts the stops, merges every stop closer than tolerance to the cluster it joins (the cluster keeps its lowest
        /// priority stop, ties going to the earlier given one) and checks the chain still has two stops and a length.
        /// A cluster is measured from its first stop, so a run of stops each just under tolerance apart cannot creep.
        /// </summary>
        public static ChainBuildResult Build(IEnumerable<ChainStop> stops, double tolerance)
        {
            if (tolerance < 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
            var result = new ChainBuildResult();
            var ordered = (stops ?? Enumerable.Empty<ChainStop>())
                .Select((s, i) => new { s, i })
                .Where(x => x.s != null && !double.IsNaN(x.s.Position) && !double.IsInfinity(x.s.Position))
                .OrderBy(x => x.s.Position).ThenBy(x => x.s.Priority).ThenBy(x => x.i)
                .Select(x => x.s).ToList();

            var i0 = 0;
            while (i0 < ordered.Count)
            {
                var anchor = ordered[i0].Position;
                var cluster = new List<ChainStop>();
                var j = i0;
                while (j < ordered.Count && ordered[j].Position - anchor <= tolerance) cluster.Add(ordered[j++]);
                var best = cluster.OrderBy(s => s.Priority).First();
                result.Stops.Add(best);
                foreach (var s in cluster)
                    if (!ReferenceEquals(s, best)) result.Merged.Add(new MergedStop(s, best));
                i0 = j;
            }

            if (result.Stops.Count < 2)
                result.SkipReason = result.Stops.Count == 0 ? "no references found" : "only one distinct reference position";
            else if (result.Stops[result.Stops.Count - 1].Position - result.Stops[0].Position <= tolerance)
                result.SkipReason = "zero-length chain";
            return result;
        }

        public static IReadOnlyList<double> Segments(IReadOnlyList<double> positions)
        {
            var segments = new List<double>();
            for (var i = 1; i < positions.Count; i++) segments.Add(positions[i] - positions[i - 1]);
            return segments;
        }

        /// <summary>True when two chains stop at the same positions (within tolerance): the second would only repeat the first.</summary>
        public static bool SameChain(IReadOnlyList<double> a, IReadOnlyList<double> b, double tolerance)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (var i = 0; i < a.Count; i++)
                if (Math.Abs(a[i] - b[i]) > tolerance) return false;
            return true;
        }
    }

    /// <summary>A face seen edge-on in a plan or section: a level along the looking axis and an extent across it.</summary>
    public sealed class FaceSpan
    {
        public FaceSpan(double level, double min, double max, int key)
        {
            Level = level;
            Min = Math.Min(min, max);
            Max = Math.Max(min, max);
            Key = key;
        }

        /// <summary>Coordinate along the axis the face's normal points (e.g. Y for a face whose normal is +Y).</summary>
        public double Level { get; }

        public double Min { get; }
        public double Max { get; }
        public int Key { get; }
    }

    public sealed class VisiblePiece
    {
        public VisiblePiece(FaceSpan face, double min, double max)
        {
            Face = face;
            Min = min;
            Max = max;
        }

        public FaceSpan Face { get; }
        public double Min { get; }
        public double Max { get; }
    }

    public static class Skyline
    {
        /// <summary>
        /// Looking from far out along +axis (outwardSign +1) or -axis (-1) back at faces whose normals point that way,
        /// returns the parts of each face that no nearer face hides. Pieces shorter than minPiece are ignored.
        /// The result is ordered along the extent.
        /// </summary>
        public static List<VisiblePiece> Visible(IEnumerable<FaceSpan> faces, int outwardSign, double minPiece)
        {
            if (outwardSign != 1 && outwardSign != -1) throw new ArgumentOutOfRangeException(nameof(outwardSign));
            var ordered = (faces ?? Enumerable.Empty<FaceSpan>()).Where(f => f != null && f.Max > f.Min)
                .OrderByDescending(f => f.Level * outwardSign).ThenBy(f => f.Min).ToList();
            var covered = new List<double[]>();
            var pieces = new List<VisiblePiece>();
            foreach (var face in ordered)
            {
                foreach (var gap in Subtract(face.Min, face.Max, covered))
                    if (gap[1] - gap[0] > minPiece) pieces.Add(new VisiblePiece(face, gap[0], gap[1]));
                covered = Union(covered, face.Min, face.Max);
            }
            return pieces.OrderBy(p => p.Min).ToList();
        }

        private static IEnumerable<double[]> Subtract(double min, double max, List<double[]> covered)
        {
            var start = min;
            foreach (var c in covered)
            {
                if (c[1] <= start) continue;
                if (c[0] >= max) break;
                if (c[0] > start) yield return new[] { start, Math.Min(c[0], max) };
                start = Math.Max(start, c[1]);
                if (start >= max) yield break;
            }
            if (start < max) yield return new[] { start, max };
        }

        private static List<double[]> Union(List<double[]> covered, double min, double max)
        {
            var all = covered.Concat(new[] { new[] { min, max } }).OrderBy(c => c[0]).ToList();
            var merged = new List<double[]>();
            foreach (var c in all)
            {
                if (merged.Count > 0 && c[0] <= merged[merged.Count - 1][1])
                    merged[merged.Count - 1][1] = Math.Max(merged[merged.Count - 1][1], c[1]);
                else
                    merged.Add(new[] { c[0], c[1] });
            }
            return merged;
        }
    }

    public static class Raycast
    {
        /// <summary>
        /// The nearest face strictly beyond origin in direction sign (+1 / -1) along the axis whose extent contains
        /// across (with tolerance). Null when none. The caller passes only faces that face back toward the origin.
        /// </summary>
        public static FaceSpan Nearest(IEnumerable<FaceSpan> faces, double origin, double across, int sign, double tolerance)
        {
            if (sign != 1 && sign != -1) throw new ArgumentOutOfRangeException(nameof(sign));
            return (faces ?? Enumerable.Empty<FaceSpan>())
                .Where(f => f != null && (f.Level - origin) * sign > tolerance
                    && across >= f.Min - tolerance && across <= f.Max + tolerance)
                .OrderBy(f => (f.Level - origin) * sign).FirstOrDefault();
        }
    }

    public enum PlanSide
    {
        Top,
        Bottom,
        Left,
        Right
    }

    public static class SideMath
    {
        public static readonly string[] PlanSideNames = { "top", "bottom", "left", "right" };

        public static bool TryParse(string text, out PlanSide side)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "top": side = PlanSide.Top; return true;
                case "bottom": side = PlanSide.Bottom; return true;
                case "left": side = PlanSide.Left; return true;
                case "right": side = PlanSide.Right; return true;
                default: side = PlanSide.Top; return false;
            }
        }

        public static string Name(PlanSide side) => side.ToString().ToLowerInvariant();

        /// <summary>0 = X, 1 = Y: the axis a chain on this side measures (top/bottom chains are horizontal and measure X).</summary>
        public static int MeasureAxis(PlanSide side) => side == PlanSide.Top || side == PlanSide.Bottom ? 0 : 1;

        /// <summary>0 = X, 1 = Y: the axis pointing out of the building on this side.</summary>
        public static int NormalAxis(PlanSide side) => 1 - MeasureAxis(side);

        /// <summary>+1 when the side faces +X / +Y (right, top), -1 otherwise.</summary>
        public static int OutwardSign(PlanSide side) => side == PlanSide.Top || side == PlanSide.Right ? 1 : -1;

        /// <summary>
        /// Coordinate (along the side's normal axis) of the index-th chain line: offset beyond the building's outer
        /// extent on that side (max coordinate for top/right, min for bottom/left), then spacing per further chain.
        /// </summary>
        public static double ChainLine(double outerExtent, int outwardSign, double offset, double spacing, int index)
        {
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            return outerExtent + outwardSign * (offset + spacing * index);
        }

        /// <summary>The building's outer extent on a side: max for an outward +1 side, min for -1.</summary>
        public static double OuterExtent(double min, double max, int outwardSign) => outwardSign > 0 ? max : min;

        /// <summary>Formats a coordinate for reasons and keys: whole millimetres when the value is whole, else one decimal.</summary>
        public static string Mm(double value)
        {
            var whole = Math.Round(value);
            if (Math.Abs(value - whole) >= 0.05) return value.ToString("0.0", CultureInfo.InvariantCulture);
            if (whole == 0) whole = 0; // no "-0" on .NET Core
            return whole.ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
