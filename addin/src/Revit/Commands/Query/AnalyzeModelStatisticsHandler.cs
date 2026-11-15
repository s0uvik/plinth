// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Documents/AnalyzeModelStatisticsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M (PRD improvement, replaces the planned get_build_counts): groupByLevel splits
// counts per level; elementIds counts only those elements, exactly and uncapped, and lists the ones
// that no longer exist, so a build can be checked against its plan in a project that already has
// other elements; categories carry builtInCategory so results do not depend on Revit's language.

using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Query
{
    public sealed class AnalyzeModelStatisticsHandler : IRevitCommand
    {
        private const int DefaultCap = 100000;
        private const int MaxIds = 20000;

        public string Name => "analyze_model_statistics";
        public string Description =>
            "Count elements by category, optionally by level. With elementIds, count exactly those elements and list missing ones. "
            + "Without, iteration stops at maxElements (default 100000).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""maxElements"":{""type"":""integer""},
            ""groupByLevel"":{""type"":""boolean""},
            ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var byLevel = p.Value<bool?>("groupByLevel") ?? false;
            var ids = p["elementIds"]?.ToObject<long[]>();
            if (ids != null && ids.Length > MaxIds) return CommandResult.Fail("INVALID_PARAMS: at most " + MaxIds + " elementIds.");

            var counts = new Dictionary<(string category, string bic, string level), int>();
            var missing = new List<long>();
            var processed = 0;
            var truncated = false;
            var cap = p.Value<int?>("maxElements") is int requested && requested > 0 ? requested : DefaultCap;

            IEnumerable<Element> elements;
            if (ids != null)
            {
                var found = new List<Element>();
                foreach (var id in ids.Distinct())
                {
                    var element = doc.GetElement(RevitCompat.ToElementId(id));
                    if (element == null) missing.Add(id);
                    else found.Add(element);
                }
                elements = found;
            }
            else
            {
                elements = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            }

            var levelNames = new Dictionary<ElementId, string>();
            foreach (var element in elements)
            {
                if (ids == null && processed >= cap)
                {
                    truncated = true;
                    break;
                }
                processed++;
                string level = null;
                if (byLevel && element.LevelId != ElementId.InvalidElementId)
                {
                    if (!levelNames.TryGetValue(element.LevelId, out level))
                        levelNames[element.LevelId] = level = doc.GetElement(element.LevelId)?.Name;
                }
                var key = (element.Category?.Name ?? "Uncategorized", Lookup.BuiltInName(element.Category), level);
                counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
            }

            var rows = counts.OrderBy(kv => kv.Key.bic ?? kv.Key.category).ThenBy(kv => kv.Key.level)
                .Select(kv =>
                {
                    var row = new JObject { ["category"] = kv.Key.category, ["builtInCategory"] = kv.Key.bic, ["count"] = kv.Value };
                    if (byLevel) row["level"] = kv.Key.level;
                    return row;
                });

            return CommandResult.Ok(new JObject
            {
                ["projectName"] = doc.Title,
                ["elementsCounted"] = processed,
                ["truncated"] = truncated,
                ["cap"] = ids == null ? new JValue(cap) : JValue.CreateNull(),
                ["missingIds"] = ids == null ? null : new JArray(missing),
                ["categories"] = new JArray(rows)
            });
        }
    }
}
