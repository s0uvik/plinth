// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Graphics/ColorElementsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: category accepts OST_ names as well as display names and a miss lists the valid ones; colours
// are applied with a solid surface fill so surfaces show them (rvt-mcp set only the pattern colour); groups are
// ordered by value and doubles group in wire units (mm, m2, m3, degrees; rvt-mcp grouped raw feet); the active view
// must allow graphic overrides and only elements visible in it are coloured (rvt-mcp overrode every element of the
// category in the project); each group lists its element IDs (capped) so the colours can be undone or reused.

using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Modify
{
    public sealed class ColorElementsHandler : IRevitCommand
    {
        private static readonly Color[] Palette =
        {
            new Color(255, 0, 0), new Color(0, 128, 255), new Color(0, 200, 0), new Color(255, 165, 0), new Color(128, 0, 255),
            new Color(255, 255, 0), new Color(0, 200, 200), new Color(200, 0, 100), new Color(100, 200, 0), new Color(200, 100, 0)
        };

        public string Name => "color_elements";
        public string Description => "In the active view, colour every element of a category (OST_Walls or display name) by the value of a parameter, "
            + "one colour per distinct value (10 colours, repeating).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""category"":{""type"":""string""},
            ""parameterName"":{""type"":""string""},
            ""max_groups"":{""type"":""integer""}},""required"":[""category"",""parameterName""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail(ModifyHelpers.NoDocument);
            var maxGroups = p.Value<int?>("max_groups") ?? 100;
            if (maxGroups < 1 || maxGroups > 500) return CommandResult.Fail("INVALID_PARAMS: max_groups must be between 1 and 500.");
            var view = doc.ActiveView;
            if (view == null || !view.AreGraphicsOverridesAllowed())
                return CommandResult.Fail("VIEW_NOT_SUPPORTED: the active view does not allow graphic overrides; open a plan, section or 3D view.");
            if (!ModifyHelpers.TryCategory(doc, p.Value<string>("category"), out var category, out var failure)) return failure;
            var paramName = p.Value<string>("parameterName");

            var groups = new FilteredElementCollector(doc, view.Id).OfCategoryId(category.Id).WhereElementIsNotElementType().ToList()
                .GroupBy(e => Key(doc, e.LookupParameter(paramName)))
                .OrderBy(g => g.Key)
                .ToList();
            if (groups.Count == 0) return CommandResult.Fail("NOTHING_TO_COLOR: the active view shows no " + category.Name + ".");

            var result = ModifyHelpers.InTransaction(doc, "Color elements by parameter", () =>
            {
                for (var i = 0; i < groups.Count; i++)
                {
                    var ogs = ModifyHelpers.ColorOverride(doc, Palette[i % Palette.Length]);
                    foreach (var e in groups[i]) view.SetElementOverrides(e.Id, ogs);
                }
                return CommandResult.Ok(null);
            });
            if (!result.Success) return result;

            return CommandResult.Ok(new JObject
            {
                ["view"] = view.Name,
                ["category"] = category.Name,
                ["coloredCount"] = groups.Sum(g => g.Count()),
                ["group_count"] = groups.Count,
                ["groups_truncated"] = groups.Count > maxGroups,
                ["groups"] = new JArray(groups.Take(maxGroups).Select((g, i) =>
                {
                    var c = Palette[i % Palette.Length];
                    return new JObject
                    {
                        ["value"] = g.Key,
                        ["count"] = g.Count(),
                        ["color"] = "RGB(" + c.Red + "," + c.Green + "," + c.Blue + ")",
                        ["elementIds"] = new JArray(g.Take(200).Select(e => RevitCompat.GetId(e.Id)))
                    };
                }))
            });
        }

        private static string Key(Document doc, Parameter parameter)
        {
            if (parameter == null) return "(no parameter)";
            switch (parameter.StorageType)
            {
                case StorageType.String: return string.IsNullOrEmpty(parameter.AsString()) ? "(empty)" : parameter.AsString();
                case StorageType.Integer: return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
                case StorageType.Double:
                    return ModifyHelpers.ToWire(parameter.AsDouble(), ModifyHelpers.WireUnit(parameter)).ToString("0.##", CultureInfo.InvariantCulture);
                case StorageType.ElementId:
                    var id = parameter.AsElementId();
                    return doc.GetElement(id)?.Name ?? RevitCompat.GetId(id).ToString(CultureInfo.InvariantCulture);
                default: return "(unknown)";
            }
        }
    }
}
