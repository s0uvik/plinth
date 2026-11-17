// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), the private helpers
// (ResolveCategories, ExtractSolids, BboxesOverlap, GetBboxOverlap, detail-level parsing) repeated in
// src/shared/Handlers/Geometry/*.cs and src/shared/Handlers/Workflows/ClashDetectionHandler.cs, commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for Plinth: one shared copy; an unresolved category is an error listing the valid names
// (rvt-mcp skipped it silently); mm through Mm; ElementIds through RevitCompat.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Geometry
{
    using View = Autodesk.Revit.DB.View;

    internal static class GeometryHelpers
    {
        public const double M2PerFt2 = 0.09290304;
        public const double M3PerFt3 = 0.0283168466;
        private const int MaxOptions = 300;

        public static JObject Point(XYZ p) => p == null ? null : new JObject
        {
            ["x"] = Mm.FromFeet(p.X),
            ["y"] = Mm.FromFeet(p.Y),
            ["z"] = Mm.FromFeet(p.Z)
        };

        public static JObject Vector(XYZ v) => new JObject
        {
            ["x"] = Math.Round(v.X, 6),
            ["y"] = Math.Round(v.Y, 6),
            ["z"] = Math.Round(v.Z, 6)
        };

        public static JObject Box(XYZ min, XYZ max) => new JObject { ["min"] = Point(min), ["max"] = Point(max) };

        public static JObject Box(BoundingBoxXYZ box) => box == null ? null : Box(box.Min, box.Max);

        /// <summary>min, max, center and size of a box, all in mm.</summary>
        public static JObject FullBox(BoundingBoxXYZ box)
        {
            if (box == null) return null;
            var result = Box(box);
            result["center"] = Point(0.5 * (box.Min + box.Max));
            result["size"] = Point(box.Max - box.Min);
            return result;
        }

        public static JObject Describe(Element e) => new JObject
        {
            ["element_id"] = RevitCompat.GetId(e.Id),
            ["name"] = e.Name ?? "",
            ["category"] = e.Category?.Name ?? "",
            ["built_in_category"] = Lookup.BuiltInName(e.Category)
        };

        /// <summary>A required, non-empty list of element ids, at most max of them, duplicates removed.</summary>
        public static bool TryIds(JObject p, string field, int max, out List<long> ids, out CommandResult failure)
        {
            ids = (p[field] as JArray)?.Select(t => t.Value<long>()).Distinct().ToList() ?? new List<long>();
            failure = null;
            if (ids.Count == 0) failure = CommandResult.Fail("INVALID_PARAMS: " + field + " must hold at least one element id.");
            else if (ids.Count > max) failure = CommandResult.Fail("INVALID_PARAMS: at most " + max + " ids in " + field + "; got " + ids.Count + ".");
            return failure == null;
        }

        public static bool TryDetailLevel(string value, out ViewDetailLevel level, out CommandResult failure)
        {
            failure = null;
            switch (value ?? "Medium")
            {
                case "Coarse": level = ViewDetailLevel.Coarse; return true;
                case "Medium": level = ViewDetailLevel.Medium; return true;
                case "Fine": level = ViewDetailLevel.Fine; return true;
            }
            level = ViewDetailLevel.Medium;
            failure = CommandResult.Fail("INVALID_PARAMS: detail_level must be Coarse, Medium or Fine.");
            return false;
        }

        public static Options GeometryOptions(ViewDetailLevel level) =>
            new Options { DetailLevel = level, ComputeReferences = false, IncludeNonVisibleObjects = false };

        /// <summary>An optional view by id; null id gives a null view and success.</summary>
        public static bool TryOptionalView(Document doc, long? id, out View view, out CommandResult failure)
        {
            view = null;
            failure = null;
            return !id.HasValue || Lookup.TryView(doc, id.Value, out view, out failure);
        }

        /// <summary>Categories by built-in name (OST_Walls, language independent) or by display name. Any miss is an error.</summary>
        public static bool TryCategories(Document doc, IEnumerable<string> names, out List<Category> categories, out CommandResult failure)
        {
            categories = new List<Category>();
            failure = null;
            var all = doc.Settings.Categories.Cast<Category>().ToList();
            var missing = new List<string>();
            foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct())
            {
                Category found = null;
                if (name.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)
                    && Enum.TryParse(name, true, out BuiltInCategory bic) && Enum.IsDefined(typeof(BuiltInCategory), bic))
                {
                    try
                    {
                        found = Category.GetCategory(doc, bic);
                    }
                    catch
                    {
                        // Not a category of this document.
                    }
                }
                if (found == null) found = all.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (found == null) missing.Add(name);
                else if (categories.All(c => c.Id != found.Id)) categories.Add(found);
            }
            if (missing.Count == 0 && categories.Count > 0) return true;
            failure = CommandResult.Fail("CATEGORY_NOT_FOUND: " + (missing.Count == 0
                    ? "give at least one category name."
                    : "no category named " + string.Join(", ", missing.Select(m => "'" + m + "'")) + ". Use names such as OST_Walls or the category's display name."),
                new JObject
                {
                    ["valid_categories"] = new JArray(all.Where(c => c.CategoryType == CategoryType.Model && Lookup.BuiltInName(c) != null)
                        .Select(Lookup.BuiltInName).OrderBy(n => n).Take(MaxOptions))
                });
            return false;
        }

        public static FilteredElementCollector Collector(Document doc, View view) =>
            view == null ? new FilteredElementCollector(doc) : new FilteredElementCollector(doc, view.Id);

        /// <summary>Solids with volume, instance geometry included (already in model coordinates).</summary>
        public static List<Solid> Solids(Element element, Options options)
        {
            var solids = new List<Solid>();
            var geometry = element.get_Geometry(options);
            if (geometry != null) CollectSolids(geometry, solids);
            return solids;
        }

        private static void CollectSolids(GeometryElement geometry, List<Solid> solids)
        {
            foreach (GeometryObject obj in geometry)
            {
                if (obj is Solid solid)
                {
                    if (solid.Volume > 1e-9 && solid.Faces.Size > 0) solids.Add(solid);
                }
                else if (obj is GeometryInstance instance)
                {
                    var nested = instance.GetInstanceGeometry();
                    if (nested != null) CollectSolids(nested, solids);
                }
                else if (obj is GeometryElement inner)
                {
                    CollectSolids(inner, solids);
                }
            }
        }

        /// <summary>Calls visit for every leaf geometry object; instances are reported to onInstance and then entered.</summary>
        public static void Walk(GeometryElement geometry, Action<GeometryObject> visit, Action<GeometryInstance> onInstance = null)
        {
            if (geometry == null) return;
            foreach (GeometryObject obj in geometry)
            {
                if (obj is GeometryInstance instance)
                {
                    onInstance?.Invoke(instance);
                    Walk(instance.GetInstanceGeometry(), visit, onInstance);
                }
                else if (obj is GeometryElement inner)
                {
                    Walk(inner, visit, onInstance);
                }
                else if (obj != null)
                {
                    visit(obj);
                }
            }
        }

        public static bool Overlap(BoundingBoxXYZ a, BoundingBoxXYZ b) =>
            a.Min.X <= b.Max.X && a.Max.X >= b.Min.X
            && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y
            && a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;

        public static JObject OverlapBox(BoundingBoxXYZ a, BoundingBoxXYZ b) => Box(
            new XYZ(Math.Max(a.Min.X, b.Min.X), Math.Max(a.Min.Y, b.Min.Y), Math.Max(a.Min.Z, b.Min.Z)),
            new XYZ(Math.Min(a.Max.X, b.Max.X), Math.Min(a.Max.Y, b.Max.Y), Math.Min(a.Max.Z, b.Max.Z)));

        /// <summary>Elements with a bounding box, at most max of them (the rest are counted in skipped).</summary>
        public static List<(Element element, BoundingBoxXYZ box)> Boxed(IEnumerable<Element> elements, View view, int max,
            out int noBox, out bool truncated)
        {
            var result = new List<(Element, BoundingBoxXYZ)>();
            noBox = 0;
            truncated = false;
            foreach (var e in elements)
            {
                BoundingBoxXYZ box = null;
                try
                {
                    box = e.get_BoundingBox(view);
                }
                catch
                {
                    // Counted as having no box.
                }
                if (box == null)
                {
                    noBox++;
                    continue;
                }
                if (result.Count >= max)
                {
                    truncated = true;
                    break;
                }
                result.Add((e, box));
            }
            return result;
        }
    }
}
