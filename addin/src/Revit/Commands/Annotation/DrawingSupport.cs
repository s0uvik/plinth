// New in B2M: Revit-side helpers shared by the drawing-set annotation commands (auto_dimension_plan,
// auto_dimension_section, label_rooms, spot_level). The Revit-free halves (chain building, skyline, text formats)
// are in addin/src/Core/Drawing.
//   - one Transaction per created item inside the command's TransactionGroup, so a Revit warning on one dimension
//     or label rolls back only that item (it is reported as skipped with the warning) and the rest are kept;
//   - text types: an existing text type of the wanted size is used; otherwise the project's default text type is
//     duplicated under a new name and only the copy's size is set (an existing type is never edited);
//   - SectionFrame: points in a section/elevation from (u along the view's right direction, absolute height).

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using B2M.Revit.Bridge;
using B2M.Revit.Core.Drawing;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Annotation
{
    // B2M.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    internal static class DrawingSupport
    {
        private const double MmPerFoot = 304.8;

        /// <summary>Feet to mm without the 0.1 mm rounding of Mm.FromFeet (used for geometry comparisons).</summary>
        public static double ToMm(double feet) => feet * MmPerFoot;

        public static double ToFeet(double mm) => mm / MmPerFoot;

        /// <summary>A non-negative number param with a default; fails with "INVALID_PARAMS" when negative or not finite.</summary>
        public static bool TryNonNegative(JObject p, string name, double fallback, out double value, out CommandResult failure)
        {
            value = p.Value<double?>(name) ?? fallback;
            failure = null;
            if (!double.IsNaN(value) && !double.IsInfinity(value) && value >= 0) return true;
            failure = CommandResult.Fail("INVALID_PARAMS: " + name + " must be 0 or more (mm).");
            return false;
        }

        /// <summary>A string array param whose items must all be in allowed; null token gives fallback.</summary>
        public static bool TryStringSet(JObject p, string name, string[] allowed, string[] fallback, out List<string> values, out CommandResult failure)
        {
            failure = null;
            values = new List<string>();
            var token = p[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                values.AddRange(fallback);
                return true;
            }
            foreach (var item in (JArray)token)
            {
                var text = item.Type == JTokenType.String ? item.Value<string>().Trim().ToLowerInvariant() : null;
                if (text == null || !allowed.Contains(text))
                {
                    failure = CommandResult.Fail("INVALID_PARAMS: " + name + " items must be one of " + string.Join(", ", allowed)
                        + "; got " + item.ToString(Newtonsoft.Json.Formatting.None) + ".");
                    return false;
                }
                if (!values.Contains(text)) values.Add(text);
            }
            if (values.Count > 0) return true;
            failure = CommandResult.Fail("INVALID_PARAMS: " + name + " cannot be empty; use any of " + string.Join(", ", allowed) + ".");
            return false;
        }

        /// <summary>
        /// Runs one write in its own transaction. Returns null on success, else why it was not kept: an exception
        /// message, or a Revit warning/error that rolled the transaction back (the failures also go into the envelope).
        /// </summary>
        public static string RunItem(Document doc, string title, Action body)
        {
            using (var tx = new Transaction(doc, title))
            {
                try
                {
                    tx.Start();
                    body();
                    var status = tx.Commit();
                    return status == TransactionStatus.Committed
                        ? null
                        : "Revit raised a warning or error and rolled this item back (see failures).";
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return ex.Message;
                }
            }
        }

        /// <summary>Ends a command's TransactionGroup: keeps the work as one undo step if anything was created, else rolls back.</summary>
        public static void Finish(TransactionGroup group, bool keep)
        {
            if (!group.HasStarted() || group.HasEnded()) return;
            if (keep) group.Assimilate();
            else group.RollBack();
        }

        /// <summary>Segment values in mm as Revit measured them (one value for a single-segment dimension).</summary>
        public static JArray Values(Dimension dimension)
        {
            var values = new JArray();
            if (dimension.NumberOfSegments > 1)
            {
                foreach (DimensionSegment segment in dimension.Segments)
                    values.Add(segment.Value.HasValue ? Math.Round(ToMm(segment.Value.Value), 1) : (double?)null);
            }
            else
            {
                values.Add(dimension.Value.HasValue ? Math.Round(ToMm(dimension.Value.Value), 1) : (double?)null);
            }
            return values;
        }

        public static JArray Stops(IEnumerable<ChainStop> stops) =>
            new JArray(stops.Select(s => new JObject { ["position_mm"] = Math.Round(s.Position, 1), ["source"] = s.Source }));

        public static JArray Merged(IEnumerable<MergedStop> merged) =>
            new JArray(merged.Select(m => new JObject
            {
                ["dropped"] = m.Dropped.Source,
                ["position_mm"] = Math.Round(m.Dropped.Position, 1),
                ["kept"] = m.KeptInstead.Source
            }));

        public static JArray Segments(ChainBuildResult chain) =>
            new JArray(chain.Segments.Select(s => Math.Round(s, 1)));

        /// <summary>The linear dimension type by id, or null for the project default.</summary>
        public static bool TryDimensionType(Document doc, long? id, out DimensionType type, out CommandResult failure)
        {
            type = null;
            failure = null;
            if (!id.HasValue) return true;
            type = doc.GetElement(RevitCompat.ToElementId(id.Value)) as DimensionType;
            if (type != null && type.StyleType == DimensionStyleType.Linear) return true;
            type = null;
            failure = CommandResult.Fail("TYPE_NOT_FOUND: no linear dimension type has id " + id.Value + ".", new JObject
            {
                ["valid_dimension_types"] = new JArray(new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                    .Where(t => t.StyleType == DimensionStyleType.Linear).OrderBy(t => t.Name).Take(100)
                    .Select(t => new JObject { ["id"] = RevitCompat.GetId(t.Id), ["name"] = t.Name }))
            });
            return false;
        }

        // ---- text types ----

        /// <summary>The text type a command will use: an existing one, or the name and source of a copy to make.</summary>
        internal sealed class TextTypePlan
        {
            public TextNoteType Existing;
            public TextNoteType DuplicateFrom;
            public string NewName;
            public double SizeMm;
            public TextNoteType Result;

            public bool Created => Existing == null;

            public JObject ToJson() => new JObject
            {
                ["id"] = Result == null ? (long?)null : RevitCompat.GetId(Result.Id),
                ["name"] = Result?.Name ?? NewName,
                ["size_mm"] = SizeMm,
                ["created"] = Created,
                ["duplicated_from"] = Created ? DuplicateFrom?.Name : null
            };
        }

        public static double TextSizeMm(TextNoteType type)
        {
            var p = type.get_Parameter(BuiltInParameter.TEXT_SIZE);
            return p == null || !p.HasValue ? double.NaN : ToMm(p.AsDouble());
        }

        /// <summary>
        /// Before any transaction. text_type_name given: that type, as is (its size is reported, not changed).
        /// Otherwise a text type whose size is sizeMm (the project default first), else a plan to duplicate the
        /// project default as "B2M &lt;size&gt;mm" (made unique) and set the copy's size.
        /// </summary>
        public static bool TryPlanTextType(Document doc, double sizeMm, string typeName, out TextTypePlan plan, out CommandResult failure)
        {
            plan = null;
            failure = null;
            var types = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().OrderBy(t => t.Name).ToList();
            JObject Valid() => new JObject
            {
                ["valid_text_types"] = new JArray(types.Take(100).Select(t => new JObject
                {
                    ["id"] = RevitCompat.GetId(t.Id),
                    ["name"] = t.Name,
                    ["size_mm"] = Math.Round(TextSizeMm(t), 2)
                }))
            };
            if (!string.IsNullOrEmpty(typeName))
            {
                var named = types.FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));
                if (named == null)
                {
                    failure = CommandResult.Fail("TYPE_NOT_FOUND: no text type named '" + typeName + "'.", Valid());
                    return false;
                }
                plan = new TextTypePlan { Existing = named, SizeMm = Math.Round(TextSizeMm(named), 2), Result = named };
                return true;
            }
            if (sizeMm <= 0)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: text_size_mm must be greater than 0.");
                return false;
            }
            var fallback = doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType)) as TextNoteType;
            var match = types.Where(t => TextTypeNames.SameSize(TextSizeMm(t), sizeMm))
                .OrderBy(t => fallback != null && t.Id == fallback.Id ? 0 : 1).ThenBy(t => t.Name).FirstOrDefault();
            if (match != null)
            {
                plan = new TextTypePlan { Existing = match, SizeMm = sizeMm, Result = match };
                return true;
            }
            var source = fallback ?? types.FirstOrDefault();
            if (source == null)
            {
                failure = CommandResult.Fail("NO_TEXT_TYPE: the project has no text type to duplicate.");
                return false;
            }
            plan = new TextTypePlan
            {
                DuplicateFrom = source,
                NewName = TextTypeNames.Unique(TextTypeNames.ForSize(sizeMm), types.Select(t => t.Name)),
                SizeMm = sizeMm
            };
            return true;
        }

        /// <summary>Inside a transaction: makes the planned copy (only the copy's size is set) and returns the type to use.</summary>
        public static TextNoteType ApplyTextType(TextTypePlan plan)
        {
            if (plan.Result != null) return plan.Result;
            var copy = plan.DuplicateFrom.Duplicate(plan.NewName) as TextNoteType
                ?? throw new InvalidOperationException("Revit did not duplicate text type '" + plan.DuplicateFrom.Name + "'.");
            var size = copy.get_Parameter(BuiltInParameter.TEXT_SIZE);
            if (size == null || size.IsReadOnly) throw new InvalidOperationException("the copied text type's size cannot be set.");
            size.Set(ToFeet(plan.SizeMm));
            plan.Result = copy;
            return copy;
        }
    }

    /// <summary>
    /// A section or elevation seen flat: u runs along the view's right direction from the view origin, heights are
    /// absolute model Z. Points are on the view plane through the view origin.
    /// </summary>
    internal sealed class SectionFrame
    {
        private SectionFrame(View view)
        {
            View = view;
            Origin = view.Origin;
            Right = view.RightDirection;
            Up = view.UpDirection;
        }

        public View View { get; }
        public XYZ Origin { get; }
        public XYZ Right { get; }
        public XYZ Up { get; }

        public static bool TryCreate(View view, out SectionFrame frame, out CommandResult failure)
        {
            frame = null;
            failure = null;
            if (!(view is ViewSection) || (view.ViewType != ViewType.Section && view.ViewType != ViewType.Elevation && view.ViewType != ViewType.Detail))
            {
                failure = CommandResult.Fail("NOT_A_SECTION_VIEW: view '" + view.Name + "' is a " + view.ViewType
                    + "; this command needs a section, elevation or detail section view.");
                return false;
            }
            if (Math.Abs(view.UpDirection.Z) < 0.9999)
            {
                failure = CommandResult.Fail("VIEW_NOT_VERTICAL: view '" + view.Name + "' does not have a vertical up direction.");
                return false;
            }
            frame = new SectionFrame(view);
            return true;
        }

        /// <summary>The point on the view plane at u (mm along right) and absolute height zMm.</summary>
        public XYZ Point(double uMm, double zMm)
        {
            var p = Origin + Right * DrawingSupport.ToFeet(uMm);
            return p + Up * ((DrawingSupport.ToFeet(zMm) - p.Z) / Up.Z);
        }

        public double U(XYZ p) => DrawingSupport.ToMm((p - Origin).DotProduct(Right));

        /// <summary>u range of a bounding box (all eight corners), or null.</summary>
        public double[] URange(BoundingBoxXYZ box)
        {
            if (box == null) return null;
            var t = box.Transform ?? Transform.Identity;
            var us = new List<double>();
            foreach (var x in new[] { box.Min.X, box.Max.X })
                foreach (var y in new[] { box.Min.Y, box.Max.Y })
                    foreach (var z in new[] { box.Min.Z, box.Max.Z })
                        us.Add(U(t.OfPoint(new XYZ(x, y, z))));
            return new[] { us.Min(), us.Max() };
        }

        /// <summary>The crop region's u range when the crop is active, else null.</summary>
        public double[] CropURange() => View.CropBoxActive ? URange(View.CropBox) : null;

        /// <summary>
        /// The u range of the given elements as seen in the view (their bounding boxes), clipped to an active crop.
        /// Null when none has a box.
        /// </summary>
        public double[] ModelURange(IEnumerable<Element> elements)
        {
            double min = double.MaxValue, max = double.MinValue;
            foreach (var e in elements)
            {
                var r = URange(e.get_BoundingBox(View) ?? e.get_BoundingBox(null));
                if (r == null) continue;
                min = Math.Min(min, r[0]);
                max = Math.Max(max, r[1]);
            }
            if (min > max) return null;
            var crop = CropURange();
            if (crop != null)
            {
                min = Math.Max(min, crop[0]);
                max = Math.Min(max, crop[1]);
                if (min > max) return null;
            }
            return new[] { min, max };
        }
    }
}
