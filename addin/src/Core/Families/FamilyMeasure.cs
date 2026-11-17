// New in Plinth (Step 10, fit-out families first): the Revit-free parts of measure_family_types and of placing a
// level-based family at its height. Some level-based families take the point's z (the level's elevation plus the
// offset asked for) as their offset above the level, so they stand the level's elevation too high. measure_family_types
// reports that as base_offset_mm; FamilyPlacer puts such an instance back at the height asked for.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Plinth.Revit.Core.Families
{
    public static class FamilyMeasure
    {
        /// <summary>Millimetres within which two heights are the same.</summary>
        public const double ToleranceMm = 1.0;

        /// <summary>
        /// How far above the point asked for Revit put the family's origin, from what the instance reports: the offset it
        /// was asked for (above the level), the offset parameter it got, and its location point's height above the level
        /// (either may be unknown). Only an extra exactly the level's elevation counts (the point's z taken as the
        /// offset); anything else is the family's own geometry, not a placing quirk.
        /// </summary>
        public static double BaseOffsetMm(double wantedOffsetMm, double? parameterOffsetMm, double? locationOffsetMm, double levelElevationMm)
        {
            if (Math.Abs(levelElevationMm) <= ToleranceMm) return 0;
            foreach (var actual in new[] { parameterOffsetMm, locationOffsetMm })
            {
                if (!actual.HasValue) continue;
                var extra = actual.Value - wantedOffsetMm;
                if (Math.Abs(extra - levelElevationMm) <= ToleranceMm) return levelElevationMm;
            }
            return 0;
        }

        /// <summary>
        /// How far an instance still is below the height asked for above its level (mm; negative when above it): 0 within
        /// <see cref="ToleranceMm"/>, the whole height when Revit gives it no location. Used to raise a work-plane-based
        /// family off the level's plane, each try checked by where Revit then puts it.
        /// </summary>
        public static double HeightStillToGoMm(double wantedAboveLevelMm, double? actualAboveLevelMm)
        {
            if (!actualAboveLevelMm.HasValue) return wantedAboveLevelMm;
            var left = wantedAboveLevelMm - actualAboveLevelMm.Value;
            return Math.Abs(left) <= ToleranceMm ? 0 : left;
        }

        /// <summary>A box corner relative to the point asked for, in mm rounded to 0.1.</summary>
        public static double[] Relative(double[] cornerMm, double[] pointMm) =>
            cornerMm.Select((v, i) => Math.Round(v - pointMm[i], 1)).ToArray();

        /// <summary>
        /// The level to measure on when none is named: the lowest above elevation 0 (so a family that takes the point's z
        /// as its offset shows it), else the lowest. Null when there is no level.
        /// </summary>
        public static string DefaultLevel(IEnumerable<KeyValuePair<string, double>> levelsMm)
        {
            var ordered = levelsMm.OrderBy(l => l.Value).ThenBy(l => l.Key, StringComparer.Ordinal).ToList();
            if (ordered.Count == 0) return null;
            var above = ordered.FirstOrDefault(l => l.Value > ToleranceMm);
            return above.Key ?? ordered[0].Key;
        }

        /// <summary>
        /// Why a family type of this placement type (Revit's FamilyPlacementType name) is not measured, or null when it
        /// is: level-based families stand at the level's origin, work-plane-based ones on the level's plane.
        /// </summary>
        public static string NotMeasuredReason(string placementType)
        {
            switch (placementType)
            {
                case "OneLevelBased":
                case "WorkPlaneBased":
                    return null;
                case "OneLevelBasedHosted":
                    return "hosted family (a wall or other host is needed to place it): not measured free";
                default:
                    return "placement type " + placementType + " is not placed by the fit-out: not measured";
            }
        }
    }
}
