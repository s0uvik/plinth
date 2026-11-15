// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Geometry/FindOverlappingElementsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: elements are sorted along X and only boxes that overlap in X are compared, so
// max_pairs counts real candidate comparisons instead of every pair in input order; at most 20000
// elements are boxed; mm through Mm; unknown categories list the valid names.

using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Geometry
{
    using View = Autodesk.Revit.DB.View;

    public sealed class FindOverlappingElementsHandler : IRevitCommand
    {
        private const int MaxElements = 20000;

        public string Name => "find_overlapping_elements";
        public string Description =>
            "Find pairs of elements of one category whose bounding boxes overlap (a pre-clash filter, not proof of solid intersection). "
            + "max_pairs (default 1000, up to 100000) bounds box comparisons; max_results 1-500 (default 100).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""category"":{""type"":""string""},
            ""view_id"":{""type"":""integer""},
            ""max_pairs"":{""type"":""integer""},
            ""max_results"":{""type"":""integer""}},""required"":[""category""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            var maxPairs = p.Value<int?>("max_pairs") ?? 1000;
            if (maxPairs < 1 || maxPairs > 100000) return CommandResult.Fail("INVALID_PARAMS: max_pairs must be 1 to 100000.");
            var maxResults = p.Value<int?>("max_results") ?? 100;
            if (maxResults < 1 || maxResults > 500) return CommandResult.Fail("INVALID_PARAMS: max_results must be 1 to 500.");
            if (!GeometryHelpers.TryOptionalView(doc, p.Value<long?>("view_id"), out View view, out var failure)) return failure;
            if (!GeometryHelpers.TryCategories(doc, new[] { p.Value<string>("category") }, out var categories, out failure)) return failure;
            var category = categories[0];

            var boxed = GeometryHelpers.Boxed(GeometryHelpers.Collector(doc, view).OfCategoryId(category.Id).WhereElementIsNotElementType(),
                    view, MaxElements, out var noBox, out var elementsTruncated)
                .OrderBy(x => x.box.Min.X).ToList();

            var overlaps = new JArray();
            var pairs = 0;
            string reason = elementsTruncated ? "max_elements" : null;
            for (var i = 0; i < boxed.Count; i++)
            {
                var a = boxed[i];
                for (var j = i + 1; j < boxed.Count; j++)
                {
                    var b = boxed[j];
                    if (b.box.Min.X > a.box.Max.X) break; // Sorted by min X: nothing further can overlap a.
                    if (pairs >= maxPairs) { reason = "max_pairs"; break; }
                    if (overlaps.Count >= maxResults) { reason = "max_results"; break; }
                    pairs++;
                    if (!GeometryHelpers.Overlap(a.box, b.box)) continue;
                    overlaps.Add(new JObject
                    {
                        ["a"] = new JObject { ["element_id"] = RevitCompat.GetId(a.element.Id), ["name"] = a.element.Name ?? "" },
                        ["b"] = new JObject { ["element_id"] = RevitCompat.GetId(b.element.Id), ["name"] = b.element.Name ?? "" },
                        ["overlap_box"] = GeometryHelpers.OverlapBox(a.box, b.box)
                    });
                }
                if (reason == "max_pairs" || reason == "max_results") break;
            }

            return CommandResult.Ok(new JObject
            {
                ["unit"] = "mm",
                ["strategy"] = "bounding_box",
                ["category"] = category.Name,
                ["built_in_category"] = Lookup.BuiltInName(category),
                ["scanned"] = boxed.Count,
                ["skipped_no_bbox"] = noBox,
                ["pairs_compared"] = pairs,
                ["returned"] = overlaps.Count,
                ["limit"] = maxResults,
                ["truncated"] = reason != null,
                ["truncation_reason"] = reason,
                ["overlaps"] = overlaps,
                ["warnings"] = new JArray("Bounding-box overlap is a pre-clash filter, not proof of solid intersection.")
            });
        }
    }
}
