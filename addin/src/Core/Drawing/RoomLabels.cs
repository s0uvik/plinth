// New in Plinth: the Revit-free half of label_rooms and of the text types the drawing commands find or duplicate.
//   RoomSizeText  - "3500 X 4000" from a room's bounding rectangle: each side rounded to a step (5 mm), ordered
//                   x-first, longer-first or shorter-first, whole numbers in invariant culture;
//   LabelLayout   - where a note goes under a tag: paper sizes times the view scale give model lengths;
//   TextTypeNames - the name a duplicated text type gets for a size ("Plinth 2.5mm"), made unique against the
//                   names already in the project, and size matching within a tolerance.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Plinth.Revit.Core.Drawing
{
    public static class RoomSizeText
    {
        public static readonly string[] Orders = { "x_first", "longer_first", "shorter_first" };

        /// <summary>Rounds to the nearest step (halves away from zero). A step of 0 or less leaves the value whole-mm rounded.</summary>
        public static double Round(double value, double step)
        {
            if (step <= 0) return Math.Round(value, MidpointRounding.AwayFromZero);
            return Math.Round(value / step, MidpointRounding.AwayFromZero) * step;
        }

        public static bool IsOrder(string order) => Orders.Contains(order);

        /// <summary>
        /// "<first> X <second>" from the room's X and Y extents in mm. order: x_first (X extent, then Y), longer_first,
        /// shorter_first. separator defaults to " X ".
        /// </summary>
        public static string Format(double xExtent, double yExtent, string order, double step, string separator = " X ")
        {
            if (!IsOrder(order)) throw new ArgumentException("order must be one of " + string.Join(", ", Orders) + ".", nameof(order));
            var x = Round(Math.Abs(xExtent), step);
            var y = Round(Math.Abs(yExtent), step);
            double first = x, second = y;
            if ((order == "longer_first" && y > x) || (order == "shorter_first" && y < x))
            {
                first = y;
                second = x;
            }
            return Whole(first) + (separator ?? " X ") + Whole(second);
        }

        private static string Whole(double v) =>
            Math.Abs(v - Math.Round(v)) < 1e-6
                ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture)
                : v.ToString("0.#", CultureInfo.InvariantCulture);
    }

    public static class LabelLayout
    {
        /// <summary>A length on paper (mm) as a model length (mm) in a view of scale 1:scale.</summary>
        public static double PaperToModel(double paperMm, int scale)
        {
            if (scale < 1) throw new ArgumentOutOfRangeException(nameof(scale));
            return paperMm * scale;
        }

        /// <summary>
        /// The top edge (model mm, along the view's up direction) of a note hung under a tag whose box bottom is at
        /// tagBottom: one gap below it, the gap being a paper distance. With a top-aligned note this is its insertion height.
        /// </summary>
        public static double NoteTopUnderTag(double tagBottom, double gapPaperMm, int scale) =>
            tagBottom - PaperToModel(gapPaperMm, scale);

        /// <summary>
        /// Fallback when Revit reports no tag box: the tag is centred on its head and assumed lines lines of text
        /// of textSizePaperMm with 1.5 line spacing.
        /// </summary>
        public static double EstimatedTagBottom(double tagHead, int lines, double textSizePaperMm, int scale) =>
            tagHead - PaperToModel(textSizePaperMm * 1.5 * Math.Max(1, lines) / 2.0, scale);
    }

    public static class TextTypeNames
    {
        /// <summary>"Plinth 2.5mm" for 2.5, "Plinth 3mm" for 3.</summary>
        public static string ForSize(double sizeMm) =>
            "Plinth " + Math.Round(sizeMm, 2).ToString("0.##", CultureInfo.InvariantCulture) + "mm";

        /// <summary>wanted if no existing name equals it (any case), else wanted + " (2)", " (3)", ...</summary>
        public static string Unique(string wanted, IEnumerable<string> existing)
        {
            var taken = new HashSet<string>((existing ?? Enumerable.Empty<string>()).Where(n => n != null), StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(wanted)) return wanted;
            for (var i = 2; ; i++)
            {
                var candidate = wanted + " (" + i.ToString(CultureInfo.InvariantCulture) + ")";
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        /// <summary>Text sizes match within 0.01 mm (Revit stores them in feet, so exact equality is not reliable).</summary>
        public static bool SameSize(double aMm, double bMm) => Math.Abs(aMm - bMm) <= 0.01;
    }
}
