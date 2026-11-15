// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Views/GetViewVisibilityHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: moved to the graphics toolset; ids are numbers (rvt-mcp sent strings); categories carry
// built_in_category (language independent); filters are listed in the view's order with enabled state; the view
// template reports whether it controls this view's visibility; a missing view lists the valid ones.

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

    public sealed class GetViewVisibilityHandler : IRevitCommand
    {
        public string Name => "get_view_visibility";
        public string Description => "Report a view's visibility state: hidden model categories, applied filters, detail level, discipline, scale, "
            + "view template. include_category_list adds every model category with its hidden state. view_id defaults to the active view.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""view_id"":{""type"":""integer""},
            ""include_category_list"":{""type"":""boolean""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!GraphicsHelpers.TryView(doc, p.Value<long?>("view_id"), out var view, out var failure)) return failure;
            var includeList = p.Value<bool?>("include_category_list") ?? false;

            var overridesAllowed = view.AreGraphicsOverridesAllowed();
            var filters = new JArray();
            if (overridesAllowed)
            {
                foreach (var id in view.GetOrderedFilters())
                {
                    filters.Add(new JObject
                    {
                        ["id"] = RevitCompat.GetId(id),
                        ["name"] = doc.GetElement(id)?.Name,
                        ["visible"] = view.GetFilterVisibility(id),
                        ["enabled"] = view.GetIsFilterEnabled(id)
                    });
                }
            }

            var hidden = new List<JObject>();
            var all = new List<JObject>();
            foreach (Category c in doc.Settings.Categories)
            {
                if (c == null || c.CategoryType != CategoryType.Model) continue;
                bool isHidden;
                try
                {
                    if (!view.CanCategoryBeHidden(c.Id)) continue;
                    isHidden = view.GetCategoryHidden(c.Id);
                }
                catch
                {
                    continue; // Categories some view types cannot report.
                }
                var row = GraphicsHelpers.CategoryRef(c);
                if (isHidden) hidden.Add(row);
                if (includeList)
                {
                    var full = GraphicsHelpers.CategoryRef(c);
                    full["hidden"] = isHidden;
                    all.Add(full);
                }
            }

            JObject template = null;
            if (view.ViewTemplateId != ElementId.InvalidElementId && doc.GetElement(view.ViewTemplateId) is View t)
            {
                bool? controlsVisibility = null;
                try
                {
                    var notControlled = t.GetNonControlledTemplateParameterIds();
                    controlsVisibility = !notControlled.Any(id => RevitCompat.GetId(id) == (long)BuiltInParameter.VIS_GRAPHICS_MODEL);
                }
                catch
                {
                    // Unknown when the template cannot report its controlled parameters.
                }
                template = new JObject
                {
                    ["id"] = RevitCompat.GetId(t.Id),
                    ["name"] = t.Name,
                    ["controls_model_visibility"] = controlsVisibility
                };
            }

            var result = GraphicsHelpers.ViewRef(view);
            result["detail_level"] = ReadOrNull(() => view.DetailLevel.ToString());
            result["discipline"] = ReadOrNull(() => view.Discipline.ToString());
            result["scale"] = ReadOrNull(() => view.Scale);
            result["graphics_overrides_allowed"] = overridesAllowed;
            result["view_template"] = template;
            result["applied_filter_count"] = filters.Count;
            result["applied_filters"] = filters;
            result["hidden_category_count"] = hidden.Count;
            result["hidden_categories"] = new JArray(hidden.OrderBy(h => h.Value<string>("name"), StringComparer.OrdinalIgnoreCase));
            if (includeList) result["categories"] = new JArray(all.OrderBy(h => h.Value<string>("name"), StringComparer.OrdinalIgnoreCase));
            return CommandResult.Ok(result);
        }

        private static JToken ReadOrNull<T>(Func<T> read)
        {
            try
            {
                return JToken.FromObject(read());
            }
            catch
            {
                return JValue.CreateNull(); // Views without this property (e.g. some drafting or system views).
            }
        }
    }
}
