// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Workflows/ClashDetectionHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: lives in the geometry toolset; set B is sorted along X so only boxes that overlap
// in X count against max_pairs (rvt-mcp counted every A x B pair in input order); at most 5000
// elements per side; when the two category sets share elements each pair is reported once; unknown
// categories are an error listing valid names (rvt-mcp warned and went on); mm and m3 conversions
// through Mm and the shared constants; solid extraction failures are reported per pair.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Geometry
{
    using View = Autodesk.Revit.DB.View;

    public sealed class ClashDetectionHandler : IRevitCommand
    {
        private const int MaxElementsPerSide = 5000;
        private const int MaxFailed = 100;

        public string Name => "clash_detection";
        public string Description =>
            "Clashes between elements of categories_a and categories_b: bounding-box overlap, then (default 'bbox_then_solid') an exact solid "
            + "intersection with its volume (m3). max_pairs (default 1000, up to 100000) bounds box comparisons; max_results 1-500 (default 100).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""categories_a"":{""type"":""array"",""items"":{""type"":""string""}},
            ""categories_b"":{""type"":""array"",""items"":{""type"":""string""}},
            ""view_id"":{""type"":""integer""},
            ""strategy"":{""type"":""string"",""enum"":[""bbox"",""bbox_then_solid""]},
            ""max_pairs"":{""type"":""integer""},
            ""max_results"":{""type"":""integer""}},""required"":[""categories_a"",""categories_b""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            var maxPairs = p.Value<int?>("max_pairs") ?? 1000;
            if (maxPairs < 1 || maxPairs > 100000) return CommandResult.Fail("INVALID_PARAMS: max_pairs must be 1 to 100000.");
            var maxResults = p.Value<int?>("max_results") ?? 100;
            if (maxResults < 1 || maxResults > 500) return CommandResult.Fail("INVALID_PARAMS: max_results must be 1 to 500.");
            var solidCheck = (p.Value<string>("strategy") ?? "bbox_then_solid") == "bbox_then_solid";
            if (!GeometryHelpers.TryOptionalView(doc, p.Value<long?>("view_id"), out View view, out var failure)) return failure;
            if (!GeometryHelpers.TryCategories(doc, p["categories_a"].Values<string>(), out var catsA, out failure)) return failure;
            if (!GeometryHelpers.TryCategories(doc, p["categories_b"].Values<string>(), out var catsB, out failure)) return failure;

            var setA = Collect(doc, view, catsA, out var noBoxA, out var truncA);
            var setB = Collect(doc, view, catsB, out var noBoxB, out var truncB).OrderBy(x => x.box.Min.X).ToList();
            var options = GeometryHelpers.GeometryOptions(ViewDetailLevel.Medium);
            var solidCache = new Dictionary<ElementId, List<Solid>>();
            var seen = new HashSet<(long, long)>();

            var clashes = new JArray();
            var failed = new JArray();
            int pairs = 0, candidates = 0, solidTests = 0, failedCount = 0;
            string reason = truncA || truncB ? "max_elements" : null;
            var stop = false;
            foreach (var a in setA)
            {
                foreach (var b in setB)
                {
                    if (b.box.Min.X > a.box.Max.X) break; // Sorted by min X: nothing further overlaps a.
                    if (b.box.Max.X < a.box.Min.X || a.element.Id == b.element.Id) continue;
                    var idA = RevitCompat.GetId(a.element.Id);
                    var idB = RevitCompat.GetId(b.element.Id);
                    if (!seen.Add((Math.Min(idA, idB), Math.Max(idA, idB)))) continue;
                    if (pairs >= maxPairs) { reason = "max_pairs"; stop = true; break; }
                    if (clashes.Count >= maxResults) { reason = "max_results"; stop = true; break; }
                    pairs++;
                    if (!GeometryHelpers.Overlap(a.box, b.box)) continue;
                    candidates++;

                    var intersectionFt3 = 0.0;
                    if (solidCheck)
                    {
                        solidTests++;
                        string error = null;
                        try
                        {
                            foreach (var sa in SolidsOf(a.element, options, solidCache))
                                foreach (var sb in SolidsOf(b.element, options, solidCache))
                                {
                                    try
                                    {
                                        var common = BooleanOperationsUtils.ExecuteBooleanOperation(sa, sb, BooleanOperationsType.Intersect);
                                        if (common != null && common.Volume > 1e-9) intersectionFt3 += common.Volume;
                                    }
                                    catch (Exception ex)
                                    {
                                        error = "boolean intersection failed: " + ex.Message;
                                    }
                                }
                        }
                        catch (Exception ex)
                        {
                            error = "solid extraction failed: " + ex.Message;
                        }
                        if (error != null)
                        {
                            failedCount++;
                            if (failed.Count < MaxFailed)
                                failed.Add(new JObject { ["a_element_id"] = idA, ["b_element_id"] = idB, ["error"] = error });
                            continue;
                        }
                        if (intersectionFt3 <= 1e-9) continue;
                    }

                    clashes.Add(new JObject
                    {
                        ["a"] = GeometryHelpers.Describe(a.element),
                        ["b"] = GeometryHelpers.Describe(b.element),
                        ["bbox_intersects"] = true,
                        ["solid_intersects"] = solidCheck,
                        ["intersection_volume_m3"] = Math.Round(intersectionFt3 * GeometryHelpers.M3PerFt3, 6),
                        ["bbox_overlap"] = GeometryHelpers.OverlapBox(a.box, b.box),
                        ["confidence"] = solidCheck ? "solid" : "bbox"
                    });
                }
                if (stop) break;
            }

            return CommandResult.Ok(new JObject
            {
                ["unit"] = "mm",
                ["strategy"] = solidCheck ? "bounding_box_prefilter_then_solid" : "bounding_box",
                ["categories_a"] = new JArray(catsA.Select(c => Lookup.BuiltInName(c) ?? c.Name)),
                ["categories_b"] = new JArray(catsB.Select(c => Lookup.BuiltInName(c) ?? c.Name)),
                ["scanned_a"] = setA.Count,
                ["scanned_b"] = setB.Count,
                ["skipped_no_bbox"] = noBoxA + noBoxB,
                ["pairs_compared"] = pairs,
                ["bbox_candidates"] = candidates,
                ["solid_tests"] = solidTests,
                ["returned"] = clashes.Count,
                ["limit"] = maxResults,
                ["truncated"] = reason != null,
                ["truncation_reason"] = reason,
                ["clashes"] = clashes,
                ["failed_count"] = failedCount,
                ["failed"] = failed
            });
        }

        private static List<(Element element, BoundingBoxXYZ box)> Collect(Document doc, View view, List<Category> categories,
            out int noBox, out bool truncated) =>
            GeometryHelpers.Boxed(GeometryHelpers.Collector(doc, view)
                    .WherePasses(new ElementMulticategoryFilter(categories.ConvertAll(c => c.Id))).WhereElementIsNotElementType(),
                view, MaxElementsPerSide, out noBox, out truncated);

        private static List<Solid> SolidsOf(Element element, Options options, Dictionary<ElementId, List<Solid>> cache)
        {
            if (!cache.TryGetValue(element.Id, out var solids))
                cache[element.Id] = solids = GeometryHelpers.Solids(element, options);
            return solids;
        }
    }
}
