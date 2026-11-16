// New in B2M (Step 9 T10b): the Revit-free half of the drawing-set reference work in create_view (plan view ranges),
// create_dimensions (reference kinds, duplicate and zero-gap checks) and auto_dimension_section (chain order, cut test).
//   ViewRangeRules        - a looking-down plan's view range must run top >= cut >= bottom >= depth (absolute heights);
//   ReferenceChecks       - two references in one dimension must not be the same reference nor at the same position;
//   InstanceReferenceNames- the names of Revit's FamilyInstanceReferenceType values, accepted in any case/underscore form;
//   WallSides             - "left"/"right" of a wall's start->end direction mapped to its exterior/interior side;
//   FacePick              - planar faces facing one way along a direction, outermost first;
//   SectionChains         - the order of auto_dimension_section's chains (nearest the building first) and the cut test.

using System;
using System.Collections.Generic;
using System.Linq;

namespace B2M.Revit.Core.Drawing
{
    public static class ViewRangeRules
    {
        /// <summary>The planes in order from the top down, as the request names them.</summary>
        public static readonly string[] Planes = { "top", "cut", "bottom", "depth" };

        /// <summary>
        /// Checks absolute plane heights (any unit) for a plan looking down: each given plane must be at or below the one
        /// above it (top >= cut >= bottom >= depth). Unknown planes (null, e.g. an unlimited top) are not compared.
        /// Returns null when valid, else the reason naming the planes and their heights.
        /// </summary>
        public static string Validate(double? top, double? cut, double? bottom, double? depth)
        {
            var values = new[] { top, cut, bottom, depth };
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i].HasValue && (double.IsNaN(values[i].Value) || double.IsInfinity(values[i].Value)))
                    return Planes[i] + " is not a finite height.";
            }
            for (var i = 0; i < values.Length; i++)
            {
                if (!values[i].HasValue) continue;
                for (var j = i + 1; j < values.Length; j++)
                {
                    if (!values[j].HasValue) continue;
                    if (values[j].Value > values[i].Value + 1e-9)
                        return Planes[j] + " (" + SideMath.Mm(values[j].Value) + ") is above " + Planes[i] + " ("
                            + SideMath.Mm(values[i].Value) + "); a plan's view range must run top >= cut >= bottom >= depth.";
                }
            }
            return null;
        }
    }

    /// <summary>One reference planned for a dimension: its stable representation and, when known, its position along the line.</summary>
    public sealed class PlannedReference
    {
        public PlannedReference(int index, string stable, double? position, string source)
        {
            Index = index;
            Stable = stable;
            Position = position;
            Source = source;
        }

        public int Index { get; }
        public string Stable { get; }
        public double? Position { get; }
        public string Source { get; }
    }

    public sealed class ReferenceConflict
    {
        public ReferenceConflict(int first, int second, string reason)
        {
            First = first;
            Second = second;
            Reason = reason;
        }

        public int First { get; }
        public int Second { get; }
        public string Reason { get; }
    }

    public static class ReferenceChecks
    {
        /// <summary>
        /// Pairs of references that would make a bad dimension: the same reference twice (equal stable representations)
        /// or two references at the same position along the line (within tolerance: a zero-length segment).
        /// </summary>
        public static List<ReferenceConflict> Conflicts(IEnumerable<PlannedReference> references, double tolerance)
        {
            if (tolerance < 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
            var list = (references ?? Enumerable.Empty<PlannedReference>()).Where(r => r != null).ToList();
            var conflicts = new List<ReferenceConflict>();
            for (var i = 0; i < list.Count; i++)
            {
                for (var j = i + 1; j < list.Count; j++)
                {
                    var a = list[i];
                    var b = list[j];
                    if (!string.IsNullOrEmpty(a.Stable) && string.Equals(a.Stable, b.Stable, StringComparison.Ordinal))
                        conflicts.Add(new ReferenceConflict(a.Index, b.Index, "the same reference is given twice"));
                    else if (a.Position.HasValue && b.Position.HasValue && Math.Abs(a.Position.Value - b.Position.Value) <= tolerance)
                        conflicts.Add(new ReferenceConflict(a.Index, b.Index,
                            "both are at " + SideMath.Mm(a.Position.Value) + " along the dimension line (a zero-length segment)"));
                }
            }
            return conflicts;
        }
    }

    public static class InstanceReferenceNames
    {
        /// <summary>Revit's FamilyInstanceReferenceType names that the commands accept.</summary>
        public static readonly string[] Names = { "Left", "Right", "CenterLeftRight", "Front", "Back", "CenterFrontBack", "Top", "Bottom" };

        /// <summary>"center_left_right", "CENTERLEFTRIGHT", "Center Left/Right" all give "CenterLeftRight"; false when none matches.</summary>
        public static bool TryNormalize(string text, out string name)
        {
            var key = new string((text ?? "").Where(char.IsLetter).ToArray()).ToLowerInvariant();
            if (key == "centreleftright") key = "centerleftright";
            if (key == "centrefrontback") key = "centerfrontback";
            name = Names.FirstOrDefault(n => n.ToLowerInvariant() == key);
            return name != null;
        }
    }

    public static class WallSides
    {
        public static readonly string[] Names = { "exterior", "interior", "left", "right" };

        /// <summary>
        /// The wall side ("exterior" or "interior") a face name means. left/right are relative to the wall's start->end
        /// direction seen from above; exteriorDotLeft is the wall's exterior direction dotted with that left normal.
        /// Null for an unknown name, or for left/right when the exterior is not clearly to one side.
        /// </summary>
        public static string Resolve(string face, double exteriorDotLeft)
        {
            switch ((face ?? "").Trim().ToLowerInvariant())
            {
                case "exterior": return "exterior";
                case "interior": return "interior";
                case "left":
                    if (Math.Abs(exteriorDotLeft) < 0.5) return null;
                    return exteriorDotLeft > 0 ? "exterior" : "interior";
                case "right":
                    if (Math.Abs(exteriorDotLeft) < 0.5) return null;
                    return exteriorDotLeft > 0 ? "interior" : "exterior";
                default: return null;
            }
        }
    }

    /// <summary>A planar face as seen along one direction: where it is and which way it faces.</summary>
    public sealed class FaceCandidate
    {
        public FaceCandidate(double position, double normalDot, int key)
        {
            Position = position;
            NormalDot = normalDot;
            Key = key;
        }

        /// <summary>The face's origin dotted with the direction.</summary>
        public double Position { get; }

        /// <summary>The face normal dotted with the (unit) direction: +1 faces along it, -1 against it.</summary>
        public double NormalDot { get; }

        public int Key { get; }
    }

    public static class FacePick
    {
        /// <summary>
        /// Faces whose normal is +direction (positiveEnd) or -direction, within angleTolerance of parallel (1 - |dot|),
        /// ordered outermost first: largest position for the positive end, smallest for the negative end. Faces at the
        /// same position (within tolerance) keep only the first given, so the list never repeats a plane.
        /// </summary>
        public static List<FaceCandidate> Outermost(IEnumerable<FaceCandidate> faces, bool positiveEnd, double tolerance,
            double angleTolerance = 1e-4)
        {
            var sign = positiveEnd ? 1 : -1;
            var facing = (faces ?? Enumerable.Empty<FaceCandidate>())
                .Select((f, i) => new { f, i })
                .Where(x => x.f != null && !double.IsNaN(x.f.Position) && x.f.NormalDot * sign >= 1 - angleTolerance)
                .OrderByDescending(x => x.f.Position * sign).ThenBy(x => x.i)
                .ToList();
            // Clusters of faces within tolerance of the cluster's outermost face are one plane: keep its first given face.
            var result = new List<FaceCandidate>();
            var k = 0;
            while (k < facing.Count)
            {
                var anchor = facing[k].f.Position;
                var cluster = new List<int>();
                while (k < facing.Count && Math.Abs(facing[k].f.Position - anchor) <= tolerance) cluster.Add(k++);
                result.Add(cluster.Select(c => facing[c]).OrderBy(x => x.i).First().f);
            }
            return result;
        }
    }

    public static class SectionChains
    {
        /// <summary>auto_dimension_section's chains, nearest to the building first.</summary>
        public static readonly string[] Order = { "openings", "beams", "slabs", "foundation", "levels" };

        /// <summary>The included chain names in drawing order (nearest first); unknown names are dropped.</summary>
        public static List<string> Ordered(IEnumerable<string> include)
        {
            var set = new HashSet<string>((include ?? Enumerable.Empty<string>()).Select(s => (s ?? "").Trim().ToLowerInvariant()));
            return Order.Where(set.Contains).ToList();
        }

        /// <summary>
        /// True when an element whose points lie at these signed distances from the section's cut plane (positive toward
        /// the viewer) is cut by it: it has points on both sides, or touches the plane within tolerance.
        /// </summary>
        public static bool IsCut(IEnumerable<double> signedDistances, double tolerance)
        {
            var list = (signedDistances ?? Enumerable.Empty<double>()).Where(d => !double.IsNaN(d)).ToList();
            if (list.Count == 0) return false;
            return list.Min() <= tolerance && list.Max() >= -tolerance;
        }

        /// <summary>
        /// The level an opening's heights are measured from: the highest level at or below the opening's bottom, else the
        /// lowest level. Returns the index into levelElevations (sorted or not), or -1 when there are none.
        /// </summary>
        public static int LevelBelow(IReadOnlyList<double> levelElevations, double bottom, double tolerance)
        {
            if (levelElevations == null || levelElevations.Count == 0) return -1;
            var best = -1;
            for (var i = 0; i < levelElevations.Count; i++)
            {
                if (levelElevations[i] > bottom + tolerance) continue;
                if (best < 0 || levelElevations[i] > levelElevations[best]) best = i;
            }
            if (best >= 0) return best;
            for (var i = 0; i < levelElevations.Count; i++)
                if (best < 0 || levelElevations[i] < levelElevations[best]) best = i;
            return best;
        }
    }
}
