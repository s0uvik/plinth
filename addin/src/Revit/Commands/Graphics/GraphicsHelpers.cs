// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), files
// src/shared/Handlers/Graphics/SetFilterOverridesHandler.cs, OverrideElementGraphicsHandler.cs,
// ClearElementOverridesHandler.cs and Views/SetViewPhaseHandler.cs, commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for B2M: the view, filter, category, phase, colour and override parsing that rvt-mcp repeated in
// every graphics handler lives here once; misses return the valid options; the solid fill pattern is found
// by IsSolidFill (language independent) and its absence is an error, not a silently unset pattern.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Graphics
{
    using View = Autodesk.Revit.DB.View;

    internal static class GraphicsHelpers
    {
        public const int MaxOptions = 100;
        public const int MaxIds = 5000;
        public const int MaxReported = 100;

        /// <summary>A view (templates allowed) by ID, or the active view when no ID is given.</summary>
        public static bool TryView(Document doc, long? viewId, out View view, out CommandResult failure)
        {
            failure = null;
            if (viewId.HasValue)
            {
                view = doc.GetElement(RevitCompat.ToElementId(viewId.Value)) as View;
                if (view != null) return true;
                failure = CommandResult.Fail("VIEW_NOT_FOUND: no view has id " + viewId.Value + ".",
                    new JObject { ["valid_views"] = ViewOptions(doc) });
                return false;
            }
            view = doc.ActiveView;
            if (view != null) return true;
            failure = CommandResult.Fail("NO_ACTIVE_VIEW: Revit has no active view; pass view_id.",
                new JObject { ["valid_views"] = ViewOptions(doc) });
            return false;
        }

        /// <summary>Fails when the view type cannot carry graphic overrides or filters (sheets, schedules...).</summary>
        public static bool RequireOverrides(View view, out CommandResult failure)
        {
            failure = null;
            if (view.AreGraphicsOverridesAllowed()) return true;
            failure = CommandResult.Fail("OVERRIDES_NOT_ALLOWED: view " + RevitCompat.GetId(view.Id) + " ('" + view.Name + "', "
                + view.ViewType + ") does not allow graphic overrides or filters.");
            return false;
        }

        public static JObject ViewRef(View view) => new JObject
        {
            ["view_id"] = RevitCompat.GetId(view.Id),
            ["view_name"] = view.Name,
            ["view_type"] = view.ViewType.ToString(),
            ["is_template"] = view.IsTemplate
        };

        private static JArray ViewOptions(Document doc) =>
            new JArray(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.AreGraphicsOverridesAllowed())
                .OrderBy(v => v.Name).Take(MaxOptions)
                .Select(v => new JObject { ["id"] = RevitCompat.GetId(v.Id), ["name"] = v.Name, ["view_type"] = v.ViewType.ToString() }));

        public static bool TryFilter(Document doc, long filterId, out ParameterFilterElement filter, out CommandResult failure)
        {
            failure = null;
            filter = doc.GetElement(RevitCompat.ToElementId(filterId)) as ParameterFilterElement;
            if (filter != null) return true;
            failure = CommandResult.Fail("FILTER_NOT_FOUND: no view filter (ParameterFilterElement) has id " + filterId + ".",
                new JObject
                {
                    ["valid_filters"] = new JArray(new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement))
                        .Cast<ParameterFilterElement>().OrderBy(f => f.Name).Take(MaxOptions)
                        .Select(f => new JObject { ["id"] = RevitCompat.GetId(f.Id), ["name"] = f.Name }))
                });
            return false;
        }

        /// <summary>
        /// Resolves category names: a built-in name such as OST_Walls (language independent) or the display
        /// name in this Revit's language. Every name must resolve and pass <paramref name="allowed"/>.
        /// </summary>
        public static bool TryCategories(Document doc, IEnumerable<string> names, Func<Category, bool> allowed, string allowedWhat,
            IEnumerable<Category> options, out List<Category> categories, out CommandResult failure)
        {
            categories = new List<Category>();
            failure = null;
            var byName = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);
            foreach (Category c in doc.Settings.Categories)
                if (c != null && !string.IsNullOrEmpty(c.Name) && !byName.ContainsKey(c.Name)) byName[c.Name] = c;

            var unknown = new List<string>();
            var refused = new List<string>();
            foreach (var raw in names)
            {
                var name = raw?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                Category category = null;
                if (name.StartsWith("OST_", StringComparison.Ordinal)
                    && Enum.TryParse(name, false, out BuiltInCategory bic) && Enum.IsDefined(typeof(BuiltInCategory), bic))
                {
                    try { category = Category.GetCategory(doc, bic); }
                    catch { category = null; }
                }
                else
                {
                    byName.TryGetValue(name, out category);
                }
                if (category == null) unknown.Add(name);
                else if (!allowed(category)) refused.Add(name);
                else if (categories.All(c => c.Id != category.Id)) categories.Add(category);
            }

            if (unknown.Count == 0 && refused.Count == 0 && categories.Count > 0) return true;
            var optionList = new JArray(options.Where(c => c != null).OrderBy(c => c.Name).Take(300)
                .Select(c => new JObject { ["name"] = c.Name, ["built_in_category"] = Lookup.BuiltInName(c) }));
            if (unknown.Count > 0)
                failure = CommandResult.Fail("CATEGORY_NOT_FOUND: no category named " + string.Join(", ", unknown.Select(n => "'" + n + "'"))
                    + ". Use a built-in name such as OST_Walls or the category name shown in Revit.",
                    new JObject { ["valid_categories"] = optionList });
            else if (refused.Count > 0)
                failure = CommandResult.Fail("CATEGORY_NOT_ALLOWED: " + string.Join(", ", refused.Select(n => "'" + n + "'")) + " " + allowedWhat + ".",
                    new JObject { ["valid_categories"] = optionList });
            else
                failure = CommandResult.Fail("INVALID_PARAMS: categories must hold at least one non-empty name.");
            return false;
        }

        public static JObject CategoryRef(Category c) => new JObject
        {
            ["name"] = c.Name,
            ["built_in_category"] = Lookup.BuiltInName(c)
        };

        /// <summary>The value of a phase field: by ID, else by name. A miss lists the phases in sequence order.</summary>
        public static bool TryPhase(Document doc, long? id, string name, string field, out Phase phase, out CommandResult failure)
        {
            failure = null;
            var phases = doc.Phases.Cast<Phase>().Where(p => p != null).ToList();
            if (id.HasValue)
                phase = phases.FirstOrDefault(p => RevitCompat.GetId(p.Id) == id.Value);
            else
                phase = phases.FirstOrDefault(p => string.Equals(p.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (phase != null) return true;
            failure = CommandResult.Fail("PHASE_NOT_FOUND: " + field + ": " + (id.HasValue ? "no phase has id " + id.Value : "no phase named '" + name + "'") + ".",
                new JObject { ["valid_phases"] = PhaseOptions(phases) });
            return false;
        }

        public static JArray PhaseOptions(IEnumerable<Phase> phases) =>
            new JArray(phases.Select(p => new JObject { ["id"] = RevitCompat.GetId(p.Id), ["name"] = p.Name }));

        /// <summary>Parses "#RRGGBB" or "RRGGBB".</summary>
        public static bool TryColor(string hex, string field, out Color color, out CommandResult failure)
        {
            color = null;
            failure = null;
            var s = (hex ?? "").Trim();
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.Length == 6
                && byte.TryParse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
                && byte.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
                && byte.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                color = new Color(r, g, b);
                return true;
            }
            failure = CommandResult.Fail("INVALID_PARAMS: " + field + " '" + hex + "' is not a hex colour such as '#FF0000'.");
            return false;
        }

        /// <summary>Schema fragment for the override fields shared by set_filter_overrides and override_element_graphics.</summary>
        public const string OverrideSchemaFields =
            @"""projection_line_color"":{""type"":""string""},
            ""surface_foreground_color"":{""type"":""string""},
            ""cut_line_color"":{""type"":""string""},
            ""transparency"":{""type"":""integer""},
            ""halftone"":{""type"":""boolean""},
            ""projection_line_weight"":{""type"":""integer""}";

        /// <summary>
        /// Reads the override fields into actions applied on top of the existing settings, so unspecified
        /// properties keep their current values.
        /// </summary>
        public static bool TryOverrides(Document doc, JObject p, out List<Action<OverrideGraphicSettings>> actions,
            out List<string> set, out CommandResult failure)
        {
            actions = new List<Action<OverrideGraphicSettings>>();
            set = new List<string>();
            failure = null;

            var projection = p.Value<string>("projection_line_color");
            if (projection != null)
            {
                if (!TryColor(projection, "projection_line_color", out var c, out failure)) return false;
                actions.Add(o => o.SetProjectionLineColor(c));
                set.Add("projection_line_color");
            }

            var surface = p.Value<string>("surface_foreground_color");
            if (surface != null)
            {
                if (!TryColor(surface, "surface_foreground_color", out var c, out failure)) return false;
                var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                    .FirstOrDefault(f =>
                    {
                        try
                        {
                            var pattern = f.GetFillPattern();
                            return pattern.IsSolidFill && pattern.Target == FillPatternTarget.Drafting;
                        }
                        catch
                        {
                            return false;
                        }
                    });
                if (solid == null)
                {
                    failure = CommandResult.Fail("FILL_PATTERN_MISSING: the project has no solid drafting fill pattern, so surface_foreground_color cannot show.");
                    return false;
                }
                var solidId = solid.Id;
                actions.Add(o =>
                {
                    o.SetSurfaceForegroundPatternId(solidId);
                    o.SetSurfaceForegroundPatternColor(c);
                    o.SetSurfaceForegroundPatternVisible(true);
                });
                set.Add("surface_foreground_color");
            }

            var cut = p.Value<string>("cut_line_color");
            if (cut != null)
            {
                if (!TryColor(cut, "cut_line_color", out var c, out failure)) return false;
                actions.Add(o => o.SetCutLineColor(c));
                set.Add("cut_line_color");
            }

            var transparency = p.Value<int?>("transparency");
            if (transparency.HasValue)
            {
                if (transparency.Value < 0 || transparency.Value > 100)
                {
                    failure = CommandResult.Fail("INVALID_PARAMS: transparency must be 0 to 100.");
                    return false;
                }
                var t = transparency.Value;
                actions.Add(o => o.SetSurfaceTransparency(t));
                set.Add("transparency");
            }

            var halftone = p.Value<bool?>("halftone");
            if (halftone.HasValue)
            {
                var h = halftone.Value;
                actions.Add(o => o.SetHalftone(h));
                set.Add("halftone");
            }

            var weight = p.Value<int?>("projection_line_weight");
            if (weight.HasValue)
            {
                if (weight.Value < 1 || weight.Value > 16)
                {
                    failure = CommandResult.Fail("INVALID_PARAMS: projection_line_weight must be 1 to 16.");
                    return false;
                }
                var w = weight.Value;
                actions.Add(o => o.SetProjectionLineWeight(w));
                set.Add("projection_line_weight");
            }

            if (set.Count > 0) return true;
            failure = CommandResult.Fail("INVALID_PARAMS: give at least one of projection_line_color, surface_foreground_color, "
                + "cut_line_color, transparency, halftone, projection_line_weight.");
            return false;
        }

        /// <summary>The non-default settings in an OverrideGraphicSettings, by name.</summary>
        public static List<string> OverriddenSettings(OverrideGraphicSettings o)
        {
            var names = new List<string>();
            if (o == null) return names;
            void Check(string name, Func<bool> test)
            {
                try
                {
                    if (test()) names.Add(name);
                }
                catch
                {
                    // A property this Revit version cannot read counts as not overridden.
                }
            }
            Check("halftone", () => o.Halftone);
            Check("transparency", () => o.Transparency > 0);
            Check("detail_level", () => o.DetailLevel != ViewDetailLevel.Undefined);
            Check("projection_line_color", () => o.ProjectionLineColor.IsValid);
            Check("cut_line_color", () => o.CutLineColor.IsValid);
            Check("surface_foreground_color", () => o.SurfaceForegroundPatternColor.IsValid);
            Check("surface_background_color", () => o.SurfaceBackgroundPatternColor.IsValid);
            Check("cut_foreground_color", () => o.CutForegroundPatternColor.IsValid);
            Check("cut_background_color", () => o.CutBackgroundPatternColor.IsValid);
            Check("projection_line_weight", () => o.ProjectionLineWeight > 0);
            Check("cut_line_weight", () => o.CutLineWeight > 0);
            Check("projection_line_pattern", () => o.ProjectionLinePatternId != ElementId.InvalidElementId);
            Check("cut_line_pattern", () => o.CutLinePatternId != ElementId.InvalidElementId);
            Check("surface_foreground_pattern", () => o.SurfaceForegroundPatternId != ElementId.InvalidElementId);
            Check("surface_background_pattern", () => o.SurfaceBackgroundPatternId != ElementId.InvalidElementId);
            Check("cut_foreground_pattern", () => o.CutForegroundPatternId != ElementId.InvalidElementId);
            Check("cut_background_pattern", () => o.CutBackgroundPatternId != ElementId.InvalidElementId);
            Check("surface_foreground_hidden", () => !o.IsSurfaceForegroundPatternVisible);
            Check("surface_background_hidden", () => !o.IsSurfaceBackgroundPatternVisible);
            Check("cut_foreground_hidden", () => !o.IsCutForegroundPatternVisible);
            Check("cut_background_hidden", () => !o.IsCutBackgroundPatternVisible);
            return names;
        }

        /// <summary>Distinct IDs from an integer array, capped.</summary>
        public static bool TryIds(JObject p, string field, bool required, out List<long> ids, out CommandResult failure)
        {
            failure = null;
            ids = (p[field] as JArray)?.Select(t => t.Value<long>()).Distinct().ToList() ?? new List<long>();
            if (required && ids.Count == 0)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: " + field + " must hold at least one element id.");
                return false;
            }
            if (ids.Count > MaxIds)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: at most " + MaxIds + " " + field + " per call; split the list.");
                return false;
            }
            return true;
        }

        public static CommandResult NotCommitted() =>
            CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
    }
}
