// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Workflows/WorkflowTakeoffReportHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: the default categories are BuiltInCategory names (rvt-mcp used English names, which fail in
// other Revit languages) and an unknown category is an error listing the valid ones (rvt-mcp warned and skipped);
// output_path goes through Export/OutputFile (exact path, no overwrite unless overwrite: true); the inline
// per-element quantity rows are capped at 2000 in total (the file gets every row) so the response stays under
// 1 MiB; the "Area"/"Volume" fallback parameters are used only when their data type is area/volume; material cost
// also reads the built-in Cost parameter; rvt-mcp's output=file SQLite spill is dropped (no spill infrastructure
// in B2M). The command only reads the model; ReadOnly is false because it can write a file.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Workflows
{
    public sealed class WorkflowTakeoffReportHandler : IRevitCommand
    {
        private const int MaxInlineElementRows = 2000;
        private const int MaxCategories = 50;

        private static readonly string[] DefaultCategories =
        {
            "OST_Walls", "OST_Floors", "OST_Roofs", "OST_Ceilings", "OST_Doors", "OST_Windows",
            "OST_StructuralFraming", "OST_StructuralColumns", "OST_DuctCurves", "OST_PipeCurves"
        };

        public string Name => "workflow_takeoff_report";
        public string Description =>
            "Quantity takeoff per category (BuiltInCategory names; default walls, floors, roofs, ceilings, doors, windows, framing, columns, ducts, pipes): "
            + "element counts, areas m2, volumes m3, material areas and volumes, optional unit cost x volume. limit_per_category 1 to 5000 (default 500). "
            + "output_path (.json or .csv, exact; overwrite to replace) writes every row; the response lists at most 2000 element rows.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""categories"":{""type"":""array"",""items"":{""type"":""string""}},
            ""include_materials"":{""type"":""boolean""},
            ""include_quantities"":{""type"":""boolean""},
            ""include_cost"":{""type"":""boolean""},
            ""output_path"":{""type"":""string""},
            ""overwrite"":{""type"":""boolean""},
            ""limit_per_category"":{""type"":""integer""}}}";
        public bool ReadOnly => false;

        private sealed class Bucket
        {
            public long MaterialId;
            public string MaterialName;
            public double AreaM2;
            public double VolumeM3;
            public double? UnitCost;
            public readonly HashSet<long> ElementIds = new HashSet<long>();
            public readonly Dictionary<string, int> CategoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var names = (p["categories"]?.ToObject<string[]>() ?? new string[0]).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToArray();
            if (names.Length == 0) names = DefaultCategories;
            if (names.Length > MaxCategories) return CommandResult.Fail("INVALID_PARAMS: at most " + MaxCategories + " categories.");
            var includeMaterials = p.Value<bool?>("include_materials") ?? true;
            var includeQuantities = p.Value<bool?>("include_quantities") ?? true;
            var includeCost = p.Value<bool?>("include_cost") ?? false;
            var overwrite = p.Value<bool?>("overwrite") ?? false;
            var limit = p.Value<int?>("limit_per_category") ?? 500;
            if (limit < 1 || limit > 5000) return CommandResult.Fail("INVALID_PARAMS: limit_per_category must be 1 to 5000.");
            if (!includeMaterials && !includeQuantities && !includeCost)
                return CommandResult.Fail("INVALID_PARAMS: at least one of include_materials, include_quantities, include_cost must be true.");

            CommandResult failure;
            string outputPath = null;
            if (!string.IsNullOrEmpty(p.Value<string>("output_path"))
                && !WorkflowsHelpers.TryOutputPath(p.Value<string>("output_path"), "output_path", overwrite, out outputPath, out failure)) return failure;

            var categories = new List<Category>();
            foreach (var name in names)
            {
                if (!WorkflowsHelpers.TryCategory(doc, name, out var category, out failure)) return failure;
                if (!categories.Any(c => c.Id == category.Id)) categories.Add(category);
            }

            var warnings = new List<string>();
            var summary = new JArray();
            var quantities = new JArray();
            var allElementRows = new JArray();
            var buckets = new Dictionary<long, Bucket>();
            var inlineRows = 0;
            var inlineTruncated = false;

            foreach (var category in categories)
            {
                var elements = WorkflowsHelpers.CollectInstances(doc, category, null, limit, out var truncated);
                if (truncated) warnings.Add("Category '" + category.Name + "' was cut at " + limit + " elements.");
                double area = 0, volume = 0;
                var rows = new JArray();
                foreach (var element in elements)
                {
                    var a = Quantity(element, BuiltInParameter.HOST_AREA_COMPUTED, "Area", SpecTypeId.Area) * WorkflowsHelpers.SquareMetresPerSquareFoot;
                    var v = Quantity(element, BuiltInParameter.HOST_VOLUME_COMPUTED, "Volume", SpecTypeId.Volume) * WorkflowsHelpers.CubicMetresPerCubicFoot;
                    area += a;
                    volume += v;
                    if (includeQuantities)
                    {
                        var row = new JObject
                        {
                            ["element_id"] = RevitCompat.GetId(element.Id),
                            ["name"] = WorkflowsHelpers.SafeName(element),
                            ["category"] = category.Name,
                            ["area_m2"] = Math.Round(a, 4),
                            ["volume_m3"] = Math.Round(v, 4)
                        };
                        if (outputPath != null) allElementRows.Add(row);
                        if (inlineRows < MaxInlineElementRows)
                        {
                            rows.Add(row);
                            inlineRows++;
                        }
                        else inlineTruncated = true;
                    }
                    if (includeMaterials || includeCost) Accumulate(doc, element, category.Name, buckets, includeCost);
                }
                summary.Add(new JObject
                {
                    ["category"] = category.Name,
                    ["builtInCategory"] = Lookup.BuiltInName(category),
                    ["element_count"] = elements.Count,
                    ["area_m2"] = Math.Round(area, 4),
                    ["volume_m3"] = Math.Round(volume, 4),
                    ["truncated"] = truncated
                });
                if (includeQuantities) quantities.Add(new JObject { ["category"] = category.Name, ["elements"] = rows });
            }
            if (inlineTruncated)
                warnings.Add("The response lists the first " + MaxInlineElementRows + " element rows" + (outputPath != null ? "; the file has all of them." : "; pass output_path for all of them."));

            var materials = new JArray();
            var costFields = new JArray();
            foreach (var bucket in buckets.Values.OrderByDescending(b => b.VolumeM3).ThenByDescending(b => b.AreaM2))
            {
                var row = new JObject
                {
                    ["material_id"] = bucket.MaterialId,
                    ["material_name"] = bucket.MaterialName,
                    ["element_count"] = bucket.ElementIds.Count,
                    ["area_m2"] = Math.Round(bucket.AreaM2, 4),
                    ["volume_m3"] = Math.Round(bucket.VolumeM3, 4),
                    ["categories"] = JObject.FromObject(bucket.CategoryCounts)
                };
                if (includeCost)
                {
                    var cost = bucket.UnitCost.HasValue ? Math.Round(bucket.UnitCost.Value * bucket.VolumeM3, 2) : (double?)null;
                    row["unit_cost"] = bucket.UnitCost;
                    row["estimated_cost"] = cost;
                    if (cost.HasValue)
                        costFields.Add(new JObject
                        {
                            ["material_id"] = bucket.MaterialId,
                            ["material_name"] = bucket.MaterialName,
                            ["unit_cost"] = bucket.UnitCost.Value,
                            ["quantity_basis"] = "volume_m3",
                            ["estimated_cost"] = cost.Value
                        });
                }
                materials.Add(row);
            }

            var steps = new JArray
            {
                WorkflowsHelpers.Step("Category Quantities", "element collectors", "succeeded", "Count elements and sum areas and volumes per category.",
                    new { categories = summary.Count })
            };
            if (includeMaterials || includeCost)
                steps.Add(WorkflowsHelpers.Step("Material Takeoff", "Element.GetMaterialArea/Volume", "succeeded", "Sum material areas and volumes.",
                    new { materials = materials.Count }));

            var status = "succeeded";
            if (outputPath != null)
            {
                try
                {
                    var rows = new JArray();
                    foreach (var c in summary.OfType<JObject>()) rows.Add(Tagged(c, "category"));
                    if (includeMaterials || includeCost)
                        foreach (var m in materials.OfType<JObject>())
                        {
                            var copy = Tagged(m, "material");
                            copy["categories"] = string.Join("; ", ((JObject)m["categories"]).Properties().Select(x => x.Name + "=" + x.Value));
                            rows.Add(copy);
                        }
                    foreach (var e in allElementRows.OfType<JObject>()) rows.Add(Tagged(e, "element"));
                    var columns = new List<string>();
                    foreach (var row in rows.OfType<JObject>())
                        foreach (var prop in row.Properties())
                            if (!columns.Contains(prop.Name)) columns.Add(prop.Name);
                    WorkflowsHelpers.WriteRows(outputPath, rows, columns);
                    steps.Add(WorkflowsHelpers.Step("Export Takeoff Report", "file write", "succeeded", "Write the takeoff to " + outputPath,
                        new { output_path = outputPath, rows = rows.Count }));
                }
                catch (Exception ex)
                {
                    status = "failed";
                    steps.Add(WorkflowsHelpers.Step("Export Takeoff Report", "file write", "failed", "Write the takeoff to " + outputPath, null, ex.Message));
                }
            }

            var envelope = WorkflowsHelpers.Envelope(Name, false, status, steps, new long[0], new long[0], warnings,
                WorkflowsHelpers.Rollback("None", false, "Read-only model scan; only the optional file is written."));
            envelope["category_summary"] = summary;
            envelope["material_summary"] = materials;
            envelope["quantities"] = quantities;
            envelope["quantities_truncated"] = inlineTruncated;
            envelope["cost_fields"] = costFields;
            envelope["output_path"] = outputPath;
            return CommandResult.Ok(envelope);
        }

        private static JObject Tagged(JObject source, string type)
        {
            var copy = (JObject)source.DeepClone();
            copy.AddFirst(new JProperty("row_type", type));
            return copy;
        }

        private static double Quantity(Element element, BuiltInParameter builtIn, string name, ForgeTypeId spec)
        {
            try
            {
                var param = element.get_Parameter(builtIn);
                if (param == null)
                {
                    param = element.LookupParameter(name);
                    if (param != null && !spec.Equals(param.Definition?.GetDataType())) param = null;
                }
                if (param != null && param.StorageType == StorageType.Double && param.HasValue) return param.AsDouble();
            }
            catch
            {
                // No readable quantity counts as zero.
            }
            return 0.0;
        }

        private static void Accumulate(Document doc, Element element, string categoryName, Dictionary<long, Bucket> buckets, bool includeCost)
        {
            ICollection<ElementId> ids;
            try { ids = element.GetMaterialIds(false); }
            catch { return; }
            foreach (var materialId in ids)
            {
                if (!(doc.GetElement(materialId) is Material material)) continue;
                var rawId = RevitCompat.GetId(materialId);
                if (!buckets.TryGetValue(rawId, out var bucket))
                {
                    bucket = new Bucket { MaterialId = rawId, MaterialName = material.Name, UnitCost = includeCost ? Cost(material) : null };
                    buckets[rawId] = bucket;
                }
                try { bucket.AreaM2 += element.GetMaterialArea(materialId, false) * WorkflowsHelpers.SquareMetresPerSquareFoot; }
                catch { /* Some elements report no material area. */ }
                try { bucket.VolumeM3 += element.GetMaterialVolume(materialId) * WorkflowsHelpers.CubicMetresPerCubicFoot; }
                catch { /* Some elements report no material volume. */ }
                bucket.ElementIds.Add(RevitCompat.GetId(element.Id));
                bucket.CategoryCounts[categoryName] = bucket.CategoryCounts.TryGetValue(categoryName, out var n) ? n + 1 : 1;
            }
        }

        private static double? Cost(Material material)
        {
            var candidates = new List<Parameter> { material.get_Parameter(BuiltInParameter.ALL_MODEL_COST) };
            foreach (var name in new[] { "Cost", "Material Cost", "Unit Cost" }) candidates.Add(material.LookupParameter(name));
            foreach (var param in candidates.Where(x => x != null && x.HasValue))
            {
                try
                {
                    if (param.StorageType == StorageType.Double) return param.AsDouble();
                    if (param.StorageType == StorageType.Integer) return param.AsInteger();
                    if (param.StorageType == StorageType.String
                        && double.TryParse(param.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
                }
                catch
                {
                    // Try the next candidate.
                }
            }
            return null;
        }
    }
}
