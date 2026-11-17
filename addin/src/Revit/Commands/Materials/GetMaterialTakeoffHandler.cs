// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Materials/GetMaterialTakeoffHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: read-only (rvt-mcp listed it as a write because of an "output: file" option that its handler
// never used; that option is dropped); category_filter also takes built-in names such as OST_Walls and narrows the
// collector; areas and volumes use Revit's unit conversion; categories carry builtInCategory; materials are
// ordered by name.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Materials
{
    public sealed class GetMaterialTakeoffHandler : IRevitCommand
    {
        public string Name => "get_material_takeoff";
        public string Description =>
            "Material takeoff: area (m2) and volume (m3) per material, split by category, optionally per element. "
            + "category_filter is a built-in name (OST_Walls) or display name; material_name_pattern matches by contains. Read-only.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""category_filter"":{""type"":""string""},
            ""material_name_pattern"":{""type"":""string""},
            ""include_elements"":{""type"":""boolean""},
            ""element_limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var categoryFilter = p.Value<string>("category_filter");
            var pattern = p.Value<string>("material_name_pattern") ?? "";
            var withElements = p.Value<bool?>("include_elements") ?? false;
            var elementLimit = p.Value<int?>("element_limit") ?? 100;
            if (elementLimit < 0 || elementLimit > 10000) return CommandResult.Fail("INVALID_PARAMS: element_limit must be 0 to 10000.");

            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            if (!string.IsNullOrEmpty(categoryFilter))
            {
                Category category = null;
                if (Enum.TryParse(categoryFilter, true, out BuiltInCategory bic) && Enum.IsDefined(typeof(BuiltInCategory), bic))
                {
                    try { category = Category.GetCategory(doc, bic); } catch { category = null; }
                }
                if (category == null)
                    foreach (Category c in doc.Settings.Categories)
                        if (string.Equals(c.Name, categoryFilter, StringComparison.OrdinalIgnoreCase)) { category = c; break; }
                if (category == null)
                    return CommandResult.Fail("CATEGORY_NOT_FOUND: no category named '" + categoryFilter + "'. Use a built-in name such as OST_Walls.");
                collector = collector.OfCategoryId(category.Id);
            }

            var rows = new Dictionary<long, List<(long element, string category, string bic, double area, double volume)>>();
            var skipped = 0;
            foreach (var e in collector)
            {
                if (e.Category == null) continue;
                ICollection<ElementId> ids;
                try { ids = e.GetMaterialIds(false); }
                catch { skipped++; continue; }
                foreach (var id in ids)
                {
                    if (!(doc.GetElement(id) is Material m)) continue;
                    if (pattern.Length > 0 && m.Name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    double area, volume;
                    try
                    {
                        area = UnitUtils.ConvertFromInternalUnits(e.GetMaterialArea(id, false), UnitTypeId.SquareMeters);
                        volume = UnitUtils.ConvertFromInternalUnits(e.GetMaterialVolume(id), UnitTypeId.CubicMeters);
                    }
                    catch { skipped++; continue; }
                    var key = RevitCompat.GetId(id);
                    if (!rows.TryGetValue(key, out var list)) rows[key] = list = new List<(long, string, string, double, double)>();
                    list.Add((RevitCompat.GetId(e.Id), e.Category.Name, Lookup.BuiltInName(e.Category), area, volume));
                }
            }

            var truncated = false;
            var materials = new JArray();
            var allElements = new HashSet<long>();
            foreach (var kv in rows.Select(kv => new { kv.Key, kv.Value, Name = doc.GetElement(RevitCompat.ToElementId(kv.Key))?.Name ?? "" })
                         .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var r in kv.Value) allElements.Add(r.element);
                var o = new JObject
                {
                    ["material_id"] = kv.Key,
                    ["material_name"] = kv.Name,
                    ["total_area_m2"] = Math.Round(kv.Value.Sum(r => r.area), 2),
                    ["total_volume_m3"] = Math.Round(kv.Value.Sum(r => r.volume), 4),
                    ["element_count"] = kv.Value.Select(r => r.element).Distinct().Count(),
                    ["categories"] = new JArray(kv.Value.GroupBy(r => (r.category, r.bic)).OrderBy(g => g.Key.category).Select(g => new JObject
                    {
                        ["category"] = g.Key.category,
                        ["builtInCategory"] = g.Key.bic,
                        ["area_m2"] = Math.Round(g.Sum(r => r.area), 2),
                        ["volume_m3"] = Math.Round(g.Sum(r => r.volume), 4),
                        ["element_count"] = g.Select(r => r.element).Distinct().Count()
                    }))
                };
                if (withElements)
                {
                    if (kv.Value.Count > elementLimit) truncated = true;
                    o["elements"] = new JArray(kv.Value.Take(elementLimit).Select(r => new JObject
                    {
                        ["element_id"] = r.element,
                        ["category"] = r.category,
                        ["area_m2"] = Math.Round(r.area, 2),
                        ["volume_m3"] = Math.Round(r.volume, 4)
                    }));
                }
                materials.Add(o);
            }

            return CommandResult.Ok(new JObject
            {
                ["material_count"] = materials.Count,
                ["element_count"] = allElements.Count,
                ["skipped_elements"] = skipped,
                ["truncated_elements"] = truncated,
                ["materials"] = materials
            });
        }
    }
}
