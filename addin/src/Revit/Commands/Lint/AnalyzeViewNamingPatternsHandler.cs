// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Lint/AnalyzeViewNamingPatternsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: the analyzer is the local NamingLint port; paging of outliers is local; an outlier whose name is
// shared by several views lists all their IDs (rvt-mcp kept only the first view of a name).

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Lint
{
    using View = Autodesk.Revit.DB.View;

    public sealed class AnalyzeViewNamingPatternsHandler : IRevitCommand
    {
        public string Name => "analyze_view_naming_patterns";
        public string Description => "Infer the view-naming patterns of printable views (digits {NN}, words {Name}), the dominant one (50 % or more) "
            + "and the outliers that nearly follow it, nearest first.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""max_patterns"":{""type"":""integer""},
            ""start_outlier"":{""type"":""integer""},
            ""max_outliers"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            var maxPatterns = p.Value<int?>("max_patterns") ?? 50;
            var start = p.Value<int?>("start_outlier") ?? 0;
            var maxOutliers = p.Value<int?>("max_outliers") ?? 20;
            if (maxPatterns < 1 || maxPatterns > 500) return CommandResult.Fail("INVALID_PARAMS: max_patterns must be between 1 and 500.");
            if (start < 0 || maxOutliers < 1 || maxOutliers > 100)
                return CommandResult.Fail("INVALID_PARAMS: start_outlier must be 0 or more and max_outliers 1 to 100.");

            var byName = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.CanBePrinted)
                .GroupBy(v => v.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(v => RevitCompat.GetId(v.Id)).OrderBy(id => id).ToList(), StringComparer.Ordinal);
            var analysis = NamingLint.Analyze(byName.Keys);
            var page = analysis.Outliers.Skip(start).Take(maxOutliers).ToList();
            var next = start + page.Count;

            return CommandResult.Ok(new JObject
            {
                ["total_views"] = analysis.Total,
                ["total_pattern_count"] = analysis.Patterns.Count,
                ["patterns_truncated"] = analysis.Patterns.Count > maxPatterns,
                ["patterns"] = new JArray(analysis.Patterns.Take(maxPatterns).Select(x => new JObject
                {
                    ["pattern"] = x.pattern,
                    ["examples"] = new JArray(x.examples),
                    ["count"] = x.count,
                    ["coverage"] = x.coverage
                })),
                ["dominant"] = analysis.Dominant,
                ["total_outlier_count"] = analysis.Outliers.Count,
                ["start_outlier"] = start,
                ["returned_outlier_count"] = page.Count,
                ["outliers_truncated"] = next < analysis.Outliers.Count,
                ["next_outlier"] = next < analysis.Outliers.Count ? new JValue(next) : JValue.CreateNull(),
                ["outliers"] = new JArray(page.Select(o => new JObject
                {
                    ["id"] = byName[o.name][0],
                    ["ids"] = new JArray(byName[o.name]),
                    ["name"] = o.name,
                    ["closest_pattern"] = analysis.Dominant,
                    ["edit_distance"] = o.distance
                }))
            });
        }
    }
}
