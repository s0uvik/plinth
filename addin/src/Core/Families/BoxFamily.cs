// New in B2M (Step 10, fit-out, 2026-11-17): the Revit-free parts of create_box_family. When no family suits a fit-out
// item and the user says so (their loaded bed set was 3.5 m wide and fitted no bedroom), B2M makes a plain box family
// for it: one extrusion W x D x H with type parameters Width, Depth and Height and a Material parameter, its origin at
// the middle of its back edge (back on +Y, front facing -Y, the fit-out's convention), its base at the level. Names,
// sizes, the template per category and the box about its origin are decided here, so they are unit-tested.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace B2M.Revit.Core.Families
{
    public static class BoxFamily
    {
        public const double MaxSizeMm = 20000;
        public const int MaxNameLength = 120;
        public const string TypeSuffix = "(B2M)";

        /// <summary>The template used when the category has none of its own (the family category is then set).</summary>
        public const string GenericTemplate = "Metric Generic Model.rft";

        /// <summary>The English metric family template per built-in category (Revit's Family Templates\English folder).</summary>
        public static readonly IReadOnlyDictionary<string, string> CategoryTemplates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OST_Furniture"] = "Metric Furniture.rft",
            ["OST_FurnitureSystems"] = "Metric Furniture System.rft",
            ["OST_Casework"] = "Metric Casework.rft",
            ["OST_SpecialityEquipment"] = "Metric Specialty Equipment.rft",
            ["OST_PlumbingFixtures"] = "Metric Plumbing Fixture.rft",
            ["OST_MechanicalEquipment"] = "Metric Mechanical Equipment.rft",
            ["OST_ElectricalEquipment"] = "Metric Electrical Equipment.rft",
            ["OST_GenericModel"] = GenericTemplate,
        };

        // Characters Windows refuses in a file name (the family is saved as <name>.rfa and Revit names it after the file)
        // and those Revit refuses in a family name.
        private static readonly char[] Forbidden = "\\/:*?\"<>|{}[];`~".ToCharArray();

        /// <summary>Why the name cannot be a family name, or null when it can.</summary>
        public static string NameError(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "the family name is empty";
            if (name != name.Trim()) return "the family name starts or ends with a space";
            if (name.Length > MaxNameLength) return "the family name is longer than " + MaxNameLength + " characters";
            if (name.EndsWith(".", StringComparison.Ordinal)) return "the family name ends with a dot";
            var bad = name.Where(c => Array.IndexOf(Forbidden, c) >= 0 || char.IsControl(c)).Distinct().ToArray();
            return bad.Length == 0 ? null : "the family name contains " + string.Join(" ", bad.Select(c => "'" + c + "'"))
                + " (not allowed: \\ / : * ? \" < > | { } [ ] ; ` ~)";
        }

        /// <summary>Why the size cannot be built, or null: each of width, depth and height above 0 and at most 20 m.</summary>
        public static string SizeError(double widthMm, double depthMm, double heightMm)
        {
            foreach (var (label, value) in new[] { ("width_mm", widthMm), ("depth_mm", depthMm), ("height_mm", heightMm) })
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0 || value > MaxSizeMm)
                    return label + " must be a number of mm above 0 and at most " + MaxSizeMm.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        /// <summary>"1500 x 2000 x 450mm (B2M)": width, depth, height.</summary>
        public static string DefaultTypeName(double widthMm, double depthMm, double heightMm) =>
            Number(widthMm) + " x " + Number(depthMm) + " x " + Number(heightMm) + "mm " + TypeSuffix;

        public static string Number(double mm)
        {
            var rounded = Math.Round(mm, 1);
            return rounded.ToString(Math.Abs(rounded - Math.Round(rounded)) < 1e-9 ? "0" : "0.0", CultureInfo.InvariantCulture);
        }

        /// <summary>The templates to try for the category, in order: its own (if it has one), then the generic model.</summary>
        public static IReadOnlyList<string> Templates(string builtInCategory)
        {
            var list = new List<string>();
            if (builtInCategory != null && CategoryTemplates.TryGetValue(builtInCategory, out var own)) list.Add(own);
            if (!list.Contains(GenericTemplate)) list.Add(GenericTemplate);
            return list;
        }

        /// <summary>
        /// The box about the family's origin (mm), min then max: x across its width, centred; y from its front (-depth) to
        /// its back (0); z from the level (0) up.
        /// </summary>
        public static double[][] Box(double widthMm, double depthMm, double heightMm) => new[]
        {
            new[] { -widthMm / 2, -depthMm, 0.0 },
            new[] { widthMm / 2, 0.0, heightMm },
        };
    }
}
