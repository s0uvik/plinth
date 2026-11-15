// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Materials/SetMaterialStructuralAssetHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: the new asset starts as a copy of the material's current structural asset (same class), so
// values not given are kept (rvt-mcp started from Revit defaults and lost them); the shared asset itself is never
// edited, because other materials may use it; an unknown structural_class is refused instead of becoming
// "generic"; a taken asset_name is refused and the default name is made unique (rvt-mcp failed on a second run).

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Materials
{
    public sealed class SetMaterialStructuralAssetHandler : IRevitCommand
    {
        public string Name => "set_material_structural_asset";
        public string Description =>
            "Give a material a new isotropic structural asset: density_kg_per_m3, young_modulus_mpa, poisson_ratio (0-0.5), shear_modulus_mpa. "
            + "Values not given are copied from its current asset. structural_class (generic, concrete, metal, wood, liquid, gas, plastic) "
            + "starts a fresh asset of that class. The old asset is left untouched for other materials.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + MaterialsHelpers.MaterialSchema + @",
            ""asset_name"":{""type"":""string""},
            ""structural_class"":{""type"":""string"",""enum"":[""generic"",""concrete"",""metal"",""wood"",""liquid"",""gas"",""plastic""]},
            ""density_kg_per_m3"":{""type"":""number""},
            ""young_modulus_mpa"":{""type"":""number""},
            ""poisson_ratio"":{""type"":""number""},
            ""shear_modulus_mpa"":{""type"":""number""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!MaterialsHelpers.TryMaterial(doc, p.Value<long?>("material_id"), p.Value<string>("material_name"),
                    "material_id", "material_name", out var mat, out var failure)) return failure;

            var density = p.Value<double?>("density_kg_per_m3");
            var young = p.Value<double?>("young_modulus_mpa");
            var poisson = p.Value<double?>("poisson_ratio");
            var shear = p.Value<double?>("shear_modulus_mpa");
            var className = p.Value<string>("structural_class");
            if (!density.HasValue && !young.HasValue && !poisson.HasValue && !shear.HasValue)
                return CommandResult.Fail("INVALID_PARAMS: give at least one of density_kg_per_m3, young_modulus_mpa, poisson_ratio, shear_modulus_mpa.");
            if (density < 0 || young < 0 || shear < 0)
                return CommandResult.Fail("INVALID_PARAMS: density and moduli must not be negative.");
            if (poisson.HasValue && (poisson.Value < 0 || poisson.Value > 0.5))
                return CommandResult.Fail("INVALID_PARAMS: poisson_ratio must be 0 to 0.5.");
            if (!MaterialsHelpers.TryAssetName(doc, p.Value<string>("asset_name"), mat.Name + " Structural", out var assetName, out failure))
                return failure;

            var current = (doc.GetElement(mat.StructuralAssetId) as PropertySetElement)?.GetStructuralAsset();
            StructuralAsset asset;
            if (className != null || current == null)
            {
                var cls = (StructuralAssetClass)Enum.Parse(typeof(StructuralAssetClass), className ?? "generic", true);
                asset = new StructuralAsset(assetName, cls);
            }
            else
            {
                asset = current.Copy();
                asset.Name = assetName;
            }

            using (var tx = new Transaction(doc, "B2M: Set material structural asset"))
            {
                tx.Start();
                try
                {
                    asset.Behavior = StructuralBehavior.Isotropic;
                    if (density.HasValue) asset.Density = UnitUtils.ConvertToInternalUnits(density.Value, UnitTypeId.KilogramsPerCubicMeter);
                    if (young.HasValue)
                    {
                        var v = UnitUtils.ConvertToInternalUnits(young.Value, UnitTypeId.Megapascals);
                        asset.YoungModulus = new XYZ(v, v, v);
                    }
                    if (poisson.HasValue) asset.PoissonRatio = new XYZ(poisson.Value, poisson.Value, poisson.Value);
                    if (shear.HasValue)
                    {
                        var v = UnitUtils.ConvertToInternalUnits(shear.Value, UnitTypeId.Megapascals);
                        asset.ShearModulus = new XYZ(v, v, v);
                    }
                    var set = PropertySetElement.Create(doc, asset);
                    mat.StructuralAssetId = set.Id;
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                    return CommandResult.Ok(new JObject
                    {
                        ["updated"] = true,
                        ["material_id"] = RevitCompat.GetId(mat.Id),
                        ["based_on_existing"] = className == null && current != null,
                        ["structural_asset"] = GetMaterialPropertiesHandler.Structural(set)
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("SET_FAILED: could not set the structural asset: " + ex.Message);
                }
            }
        }
    }
}
