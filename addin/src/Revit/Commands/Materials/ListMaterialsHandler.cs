// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Materials/ListMaterialsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: limit outside 1-10000 is an error instead of being clamped; results are ordered by name.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Materials
{
    public sealed class ListMaterialsHandler : IRevitCommand
    {
        public string Name => "list_materials";
        public string Description =>
            "List materials, filtered by name_pattern and class_filter (contains, case-insensitive): id, class, category, colour, transparency, "
            + "asset ids, and with include_use_count the number of elements using each.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""name_pattern"":{""type"":""string""},
            ""class_filter"":{""type"":""string""},
            ""include_assets"":{""type"":""boolean""},
            ""include_use_count"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var namePattern = p.Value<string>("name_pattern") ?? "";
            var classFilter = p.Value<string>("class_filter") ?? "";
            var withAssets = p.Value<bool?>("include_assets") ?? true;
            var withUse = p.Value<bool?>("include_use_count") ?? false;
            var limit = p.Value<int?>("limit") ?? 1000;
            if (limit < 1 || limit > 10000) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to 10000.");

            var useCounts = new Dictionary<long, int>();
            if (withUse)
            {
                foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                {
                    ICollection<ElementId> ids;
                    try { ids = e.GetMaterialIds(false); }
                    catch { continue; }
                    foreach (var id in ids)
                    {
                        var key = RevitCompat.GetId(id);
                        useCounts[key] = useCounts.TryGetValue(key, out var n) ? n + 1 : 1;
                    }
                }
            }

            var rows = MaterialsHelpers.All(doc)
                .Where(m => namePattern.Length == 0 || m.Name.IndexOf(namePattern, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(m => classFilter.Length == 0 || (m.MaterialClass ?? "").IndexOf(classFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            return CommandResult.Ok(new JObject
            {
                ["total"] = rows.Count,
                ["returned"] = Math.Min(limit, rows.Count),
                ["limit_hit"] = rows.Count > limit,
                ["materials"] = new JArray(rows.Take(limit).Select(m =>
                {
                    var id = RevitCompat.GetId(m.Id);
                    var o = new JObject
                    {
                        ["id"] = id,
                        ["name"] = m.Name,
                        ["material_class"] = m.MaterialClass ?? "",
                        ["material_category"] = m.MaterialCategory ?? "",
                        ["color"] = MaterialsHelpers.ColorJson(m.Color),
                        ["transparency"] = m.Transparency
                    };
                    if (withAssets)
                    {
                        o["appearance_asset_id"] = MaterialsHelpers.IdOrNull(m.AppearanceAssetId);
                        o["structural_asset_id"] = MaterialsHelpers.IdOrNull(m.StructuralAssetId);
                        o["thermal_asset_id"] = MaterialsHelpers.IdOrNull(m.ThermalAssetId);
                    }
                    if (withUse) o["use_count"] = useCounts.TryGetValue(id, out var n) ? n : 0;
                    return o;
                }))
            });
        }
    }
}
