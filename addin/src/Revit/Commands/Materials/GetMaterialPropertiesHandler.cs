// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Materials/GetMaterialPropertiesHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: misses list the valid materials; identity fields are read from built-in parameters only
// (URL from ALL_MODEL_URL instead of a name lookup that fails in non-English Revit); parameter values use the
// shared read rules (lengths in mm through Mm, areas m2, volumes m3, angles degrees).

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Plinth.Revit.Commands.Parameters;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Materials
{
    public sealed class GetMaterialPropertiesHandler : IRevitCommand
    {
        public string Name => "get_material_properties";
        public string Description =>
            "Read one material (material_id or material_name): graphics, fill patterns, identity data, structural asset "
            + "(density kg/m3, moduli MPa) and thermal asset (W/(m*K), J/(kg*K)), and optionally all its parameters.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + MaterialsHelpers.MaterialSchema + @",
            ""include_assets"":{""type"":""boolean""},
            ""include_parameters"":{""type"":""boolean""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!MaterialsHelpers.TryMaterial(doc, p.Value<long?>("material_id"), p.Value<string>("material_name"),
                    "material_id", "material_name", out var mat, out var failure)) return failure;
            var withAssets = p.Value<bool?>("include_assets") ?? true;
            var withParameters = p.Value<bool?>("include_parameters") ?? true;

            var result = new JObject
            {
                ["id"] = RevitCompat.GetId(mat.Id),
                ["name"] = mat.Name,
                ["material_class"] = mat.MaterialClass ?? "",
                ["material_category"] = mat.MaterialCategory ?? "",
                ["color"] = MaterialsHelpers.ColorJson(mat.Color),
                ["transparency"] = mat.Transparency,
                ["shininess"] = mat.Shininess,
                ["smoothness"] = mat.Smoothness,
                ["use_render_appearance_for_shading"] = mat.UseRenderAppearanceForShading,
                ["patterns"] = new JObject
                {
                    ["surface_foreground_pattern_id"] = MaterialsHelpers.IdOrNull(mat.SurfaceForegroundPatternId),
                    ["surface_background_pattern_id"] = MaterialsHelpers.IdOrNull(mat.SurfaceBackgroundPatternId),
                    ["cut_foreground_pattern_id"] = MaterialsHelpers.IdOrNull(mat.CutForegroundPatternId),
                    ["cut_background_pattern_id"] = MaterialsHelpers.IdOrNull(mat.CutBackgroundPatternId)
                },
                ["identity"] = new JObject
                {
                    ["manufacturer"] = Text(mat, BuiltInParameter.ALL_MODEL_MANUFACTURER),
                    ["model"] = Text(mat, BuiltInParameter.ALL_MODEL_MODEL),
                    ["cost"] = Text(mat, BuiltInParameter.ALL_MODEL_COST),
                    ["keynote"] = Text(mat, BuiltInParameter.KEYNOTE_PARAM),
                    ["mark"] = Text(mat, BuiltInParameter.ALL_MODEL_MARK),
                    ["url"] = Text(mat, BuiltInParameter.ALL_MODEL_URL),
                    ["description"] = Text(mat, BuiltInParameter.ALL_MODEL_DESCRIPTION)
                }
            };

            if (withAssets)
            {
                var appearance = doc.GetElement(mat.AppearanceAssetId) as AppearanceAssetElement;
                result["appearance_asset"] = appearance == null ? null
                    : new JObject { ["id"] = RevitCompat.GetId(appearance.Id), ["name"] = appearance.Name };
                result["structural_asset"] = Structural(doc.GetElement(mat.StructuralAssetId) as PropertySetElement);
                result["thermal_asset"] = Thermal(doc.GetElement(mat.ThermalAssetId) as PropertySetElement);
            }

            if (withParameters)
            {
                var list = new JArray();
                foreach (Parameter parameter in mat.Parameters)
                {
                    if (parameter?.Definition == null) continue;
                    var o = ParametersHelpers.Read(doc, parameter);
                    o.AddFirst(new JProperty("name", parameter.Definition.Name));
                    o["isReadOnly"] = parameter.IsReadOnly;
                    o["hasValue"] = parameter.HasValue;
                    list.Add(o);
                }
                result["parameters"] = list;
            }
            return CommandResult.Ok(result);
        }

        private static string Text(Material mat, BuiltInParameter bip)
        {
            var parameter = mat.get_Parameter(bip);
            if (parameter == null || !parameter.HasValue) return "";
            return (parameter.StorageType == StorageType.String ? parameter.AsString() : parameter.AsValueString()) ?? "";
        }

        internal static JToken Structural(PropertySetElement set)
        {
            if (set == null) return null;
            var o = new JObject { ["id"] = RevitCompat.GetId(set.Id), ["name"] = set.Name };
            try
            {
                var asset = set.GetStructuralAsset();
                o["structural_class"] = asset.StructuralAssetClass.ToString();
                o["density_kg_per_m3"] = Math.Round(UnitUtils.ConvertFromInternalUnits(asset.Density, UnitTypeId.KilogramsPerCubicMeter), 2);
                o["young_modulus_mpa"] = Math.Round(UnitUtils.ConvertFromInternalUnits(asset.YoungModulus.X, UnitTypeId.Megapascals), 2);
                o["poisson_ratio"] = Math.Round(asset.PoissonRatio.X, 4);
                o["shear_modulus_mpa"] = Math.Round(UnitUtils.ConvertFromInternalUnits(asset.ShearModulus.X, UnitTypeId.Megapascals), 2);
            }
            catch (Exception ex)
            {
                o["error"] = ex.Message;
            }
            return o;
        }

        internal static JToken Thermal(PropertySetElement set)
        {
            if (set == null) return null;
            var o = new JObject { ["id"] = RevitCompat.GetId(set.Id), ["name"] = set.Name };
            try
            {
                var asset = set.GetThermalAsset();
                o["conductivity_w_per_m_k"] = Math.Round(UnitUtils.ConvertFromInternalUnits(asset.ThermalConductivity, UnitTypeId.WattsPerMeterKelvin), 4);
                o["specific_heat_j_per_kg_k"] = Math.Round(UnitUtils.ConvertFromInternalUnits(asset.SpecificHeat, UnitTypeId.JoulesPerKilogramDegreeCelsius), 2);
                o["emissivity"] = Math.Round(asset.Emissivity, 4);
                o["permeability"] = Math.Round(asset.Permeability, 4);
                o["density_kg_per_m3"] = Math.Round(UnitUtils.ConvertFromInternalUnits(asset.Density, UnitTypeId.KilogramsPerCubicMeter), 2);
            }
            catch (Exception ex)
            {
                o["error"] = ex.Message;
            }
            return o;
        }
    }
}
