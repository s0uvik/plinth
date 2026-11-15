// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Materials/GetMaterialQuantitiesHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: category accepts OST_ names as well as display names and a miss lists the valid ones; areas and
// volumes convert with UnitUtils (rvt-mcp used literal factors); materials group by ID, not by name, so two materials
// that share a name no longer merge; paint (GetMaterialIds(true)) is reported separately as paintedAreaM2; paging is local.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Query
{
    public sealed class GetMaterialQuantitiesHandler : IRevitCommand
    {
        public string Name => "get_material_quantities";
        public string Description => "Total material area (m2) and volume (m3) over all elements of a category (OST_Walls or display name), "
            + "largest volume first, a page at a time. material_name_filter keeps materials whose name contains the text.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""category"":{""type"":""string""},
            ""material_name_filter"":{""type"":""string""}," + QueryHelpers.PagingSchema + @"},""required"":[""category""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!QueryHelpers.TryPaging(p, 200, 1000, out var start, out var size, out var failure)) return failure;
            if (!QueryHelpers.TryCategory(doc, p.Value<string>("category"), out var category, out failure)) return failure;
            var filter = p.Value<string>("material_name_filter");

            var elements = new FilteredElementCollector(doc).OfCategoryId(category.Id).WhereElementIsNotElementType().ToList();
            var totals = new Dictionary<ElementId, double[]>(); // area, volume, painted area, elements with the material in their geometry
            foreach (var element in elements)
            {
                foreach (var id in element.GetMaterialIds(false))
                    Add(totals, id, element.GetMaterialArea(id, false), element.GetMaterialVolume(id), 0, 1);
                foreach (var id in element.GetMaterialIds(true))
                    Add(totals, id, 0, 0, element.GetMaterialArea(id, true), 0);
            }

            var rows = totals.Select(kv => new { id = kv.Key, name = QueryHelpers.NameOf(doc.GetElement(kv.Key)) ?? "Unknown", t = kv.Value })
                .Where(r => string.IsNullOrWhiteSpace(filter) || r.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(r => r.t[1]).ThenBy(r => r.name)
                .ToList();
            var result = QueryHelpers.Page(rows, start, size, "materials", r => new JObject
            {
                ["materialId"] = RevitCompat.GetId(r.id),
                ["material"] = r.name,
                ["totalAreaM2"] = Math.Round(UnitUtils.ConvertFromInternalUnits(r.t[0], UnitTypeId.SquareMeters), 2),
                ["totalVolumeM3"] = Math.Round(UnitUtils.ConvertFromInternalUnits(r.t[1], UnitTypeId.CubicMeters), 4),
                ["paintedAreaM2"] = Math.Round(UnitUtils.ConvertFromInternalUnits(r.t[2], UnitTypeId.SquareMeters), 2),
                ["elementCount"] = (int)r.t[3]
            });
            result["category"] = category.Name;
            result["elementCount"] = elements.Count;
            return CommandResult.Ok(result);
        }

        private static void Add(Dictionary<ElementId, double[]> totals, ElementId id, double area, double volume, double painted, int count)
        {
            if (!totals.TryGetValue(id, out var t)) totals[id] = t = new double[4];
            t[0] += area;
            t[1] += volume;
            t[2] += painted;
            t[3] += count;
        }
    }
}
