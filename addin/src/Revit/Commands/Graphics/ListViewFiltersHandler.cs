// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Graphics/ListViewFiltersHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: ids are numbers (rvt-mcp sent strings); categories carry built_in_category; the view branch
// lists the overridden settings by name and keeps the view's filter order; the filter list and each filter's
// usage list are capped with truncation reported; a missing view lists the valid ones.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Graphics
{
    using View = Autodesk.Revit.DB.View;

    public sealed class ListViewFiltersHandler : IRevitCommand
    {
        private const int MaxViewsPerFilter = 50;

        public string Name => "list_view_filters";
        public string Description => "List view filter definitions with their categories. With view_id, only the filters applied to that view, "
            + "in order, with visibility, enabled state and overridden settings. include_usage maps each filter to the views using it.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""view_id"":{""type"":""integer""},
            ""include_usage"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var viewId = p.Value<long?>("view_id");
            var includeUsage = p.Value<bool?>("include_usage") ?? false;
            var limit = p.Value<int?>("limit") ?? 500;
            if (limit < 1 || limit > 5000) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to 5000.");
            if (viewId.HasValue && includeUsage) return CommandResult.Fail("INVALID_PARAMS: include_usage applies only without view_id.");

            if (viewId.HasValue)
            {
                if (!GraphicsHelpers.TryView(doc, viewId, out var view, out var failure)) return failure;
                if (!GraphicsHelpers.RequireOverrides(view, out failure)) return failure;
                var ordered = view.GetOrderedFilters();
                var rows = new JArray();
                foreach (var id in ordered.Take(limit))
                {
                    var pfe = doc.GetElement(id) as FilterElement;
                    var row = new JObject
                    {
                        ["id"] = RevitCompat.GetId(id),
                        ["name"] = pfe?.Name,
                        ["kind"] = pfe is ParameterFilterElement ? "parameter" : pfe is SelectionFilterElement ? "selection" : "other",
                        ["categories"] = pfe is ParameterFilterElement parameterFilter ? Categories(doc, parameterFilter) : new JArray(),
                        ["visible"] = view.GetFilterVisibility(id),
                        ["enabled"] = view.GetIsFilterEnabled(id),
                        ["overridden_settings"] = new JArray(GraphicsHelpers.OverriddenSettings(view.GetFilterOverrides(id)).Cast<object>())
                    };
                    rows.Add(row);
                }
                var result = GraphicsHelpers.ViewRef(view);
                result["total_filters"] = ordered.Count;
                result["returned"] = rows.Count;
                result["truncated"] = ordered.Count > rows.Count;
                result["filters"] = rows;
                return CommandResult.Ok(result);
            }

            Dictionary<long, List<View>> usage = null;
            if (includeUsage)
            {
                usage = new Dictionary<long, List<View>>();
                foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                {
                    ICollection<ElementId> ids;
                    try
                    {
                        if (!v.AreGraphicsOverridesAllowed()) continue;
                        ids = v.GetFilters();
                    }
                    catch
                    {
                        continue; // View types without filter support.
                    }
                    foreach (var id in ids)
                    {
                        var key = RevitCompat.GetId(id);
                        if (!usage.TryGetValue(key, out var list)) usage[key] = list = new List<View>();
                        list.Add(v);
                    }
                }
            }

            var filters = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>()
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var output = new JArray();
            foreach (var f in filters.Take(limit))
            {
                var row = new JObject
                {
                    ["id"] = RevitCompat.GetId(f.Id),
                    ["name"] = f.Name,
                    ["categories"] = Categories(doc, f),
                    ["rule_parameter_count"] = SafeCount(f)
                };
                if (usage != null)
                {
                    usage.TryGetValue(RevitCompat.GetId(f.Id), out var views);
                    views = views ?? new List<View>();
                    row["applied_to_view_count"] = views.Count;
                    row["applied_to_views"] = new JArray(views.OrderBy(v => v.Name).Take(MaxViewsPerFilter).Select(v => new JObject
                    {
                        ["id"] = RevitCompat.GetId(v.Id),
                        ["name"] = v.Name,
                        ["is_template"] = v.IsTemplate
                    }));
                    row["applied_to_views_truncated"] = views.Count > MaxViewsPerFilter;
                }
                output.Add(row);
            }

            return CommandResult.Ok(new JObject
            {
                ["total_filters"] = filters.Count,
                ["returned"] = output.Count,
                ["truncated"] = filters.Count > output.Count,
                ["include_usage"] = includeUsage,
                ["filters"] = output
            });
        }

        private static JArray Categories(Document doc, ParameterFilterElement f)
        {
            var rows = new List<JObject>();
            foreach (var id in f.GetCategories())
            {
                var c = Category.GetCategory(doc, id);
                rows.Add(c != null
                    ? GraphicsHelpers.CategoryRef(c)
                    : new JObject { ["name"] = null, ["built_in_category"] = null, ["id"] = RevitCompat.GetId(id) });
            }
            return new JArray(rows.OrderBy(r => r.Value<string>("name") ?? ""));
        }

        private static int SafeCount(ParameterFilterElement f)
        {
            try
            {
                return f.GetElementFilterParameters().Count;
            }
            catch
            {
                return 0;
            }
        }
    }
}
