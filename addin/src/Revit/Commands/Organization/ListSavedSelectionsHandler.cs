// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Organization/ListSavedSelectionsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: sorted by name before the limit is applied (rvt-mcp cut first, then sorted); the
// result says how many matched and whether it was truncated; per-selection element lists are capped at
// 1000 IDs each to stay under the response cap; summaries carry the built-in category name.

using System;
using System.Linq;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Organization
{
    public sealed class ListSavedSelectionsHandler : IRevitCommand
    {
        private const int MaxIdsPerSelection = 1000;

        public string Name => "list_saved_selections";
        public string Description =>
            "List saved selections (Revit selection filters) with element counts, sorted by name. nameFilter matches part of the name. "
            + "includeElementIds / includeElementSummary add the elements (up to 1000 per selection). limit 1-2000 (default 500).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""nameFilter"":{""type"":""string""},
            ""includeElementIds"":{""type"":""boolean""},
            ""includeElementSummary"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var nameFilter = p.Value<string>("nameFilter");
            var includeIds = p.Value<bool?>("includeElementIds") ?? false;
            var includeSummary = p.Value<bool?>("includeElementSummary") ?? false;
            var limit = p.Value<int?>("limit") ?? 500;
            if (limit < 1 || limit > 2000) return CommandResult.Fail("INVALID_PARAMS: limit must be between 1 and 2000.");

            var all = OrganizationHelpers.Selections(doc);
            var matched = all.Where(f => string.IsNullOrEmpty(nameFilter)
                || f.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            var rows = new JArray();
            foreach (var filter in matched.Take(limit))
            {
                var ids = filter.GetElementIds().OrderBy(RevitCompat.GetId).ToList();
                OrganizationHelpers.Resolve(doc, ids, out var found, out var stale);
                var row = new JObject
                {
                    ["selectionId"] = RevitCompat.GetId(filter.Id),
                    ["name"] = filter.Name,
                    ["count"] = ids.Count,
                    ["staleCount"] = stale.Count
                };
                if (includeIds)
                {
                    row["elementIds"] = new JArray(ids.Take(MaxIdsPerSelection).Select(RevitCompat.GetId));
                    row["elementIdsTruncated"] = ids.Count > MaxIdsPerSelection;
                }
                if (includeSummary)
                    row["elements"] = new JArray(found.Take(MaxIdsPerSelection).Select(id => OrganizationHelpers.ElementSummary(doc, doc.GetElement(id))));
                rows.Add(row);
            }

            return CommandResult.Ok(new JObject
            {
                ["count"] = all.Count,
                ["matched"] = matched.Count,
                ["returned"] = rows.Count,
                ["truncated"] = matched.Count > rows.Count,
                ["selections"] = rows
            });
        }
    }
}
