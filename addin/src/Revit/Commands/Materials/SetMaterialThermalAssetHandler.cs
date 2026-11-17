// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Materials/SetMaterialThermalAssetHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: the new asset starts as a copy of the material's current thermal asset, so values not given
// are kept (rvt-mcp started from defaults); the shared asset itself is never edited; a taken asset_name is
// refused and the default name is made unique (rvt-mcp failed on a second run).
// Duplicate before edit (TypeEditGuard): a material is shared, so it is changed only with duplicate_as (a copy named
// so gets the change; nothing else moves onto it) or confirm_edit_all (= its best-effort user count); otherwise
// MATERIAL_IN_USE before anything changes.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Materials
{
    public sealed class SetMaterialThermalAssetHandler : IRevitCommand
    {
        public string Name => "set_material_thermal_asset";
        public string Description =>
            "Give a material a new thermal asset: conductivity_w_per_m_k, specific_heat_j_per_kg_k, emissivity (0-1), permeability, "
            + "density_kg_per_m3. Values not given are copied from its current thermal asset (or a new solid asset); the old asset is left untouched. "
            + "Giving the material the new asset changes every element using it:" + TypeEditGuard.Rule;
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + MaterialsHelpers.MaterialSchema + @",
            ""asset_name"":{""type"":""string""},
            ""conductivity_w_per_m_k"":{""type"":""number""},
            ""specific_heat_j_per_kg_k"":{""type"":""number""},
            ""emissivity"":{""type"":""number""},
            ""permeability"":{""type"":""number""},
            ""density_kg_per_m3"":{""type"":""number""},
            " + TypeEditGuard.SchemaFields + @"}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!MaterialsHelpers.TryMaterial(doc, p.Value<long?>("material_id"), p.Value<string>("material_name"),
                    "material_id", "material_name", out var mat, out var failure)) return failure;

            var conductivity = p.Value<double?>("conductivity_w_per_m_k");
            var specificHeat = p.Value<double?>("specific_heat_j_per_kg_k");
            var emissivity = p.Value<double?>("emissivity");
            var permeability = p.Value<double?>("permeability");
            var density = p.Value<double?>("density_kg_per_m3");
            if (!conductivity.HasValue && !specificHeat.HasValue && !emissivity.HasValue && !permeability.HasValue && !density.HasValue)
                return CommandResult.Fail("INVALID_PARAMS: give at least one thermal value.");
            if (conductivity < 0 || specificHeat < 0 || permeability < 0 || density < 0)
                return CommandResult.Fail("INVALID_PARAMS: thermal values must not be negative.");
            if (emissivity.HasValue && (emissivity.Value < 0 || emissivity.Value > 1))
                return CommandResult.Fail("INVALID_PARAMS: emissivity must be 0 to 1.");
            if (!TypeEditGuard.TryPlan(doc, p, new[] { new SharedEdit(mat) }, out var plan, out failure)) return failure;

            if (!MaterialsHelpers.TryAssetName(doc, p.Value<string>("asset_name"), SetMaterialStructuralAssetHandler.EditName(plan, mat) + " Thermal", out var assetName, out failure))
                return failure;

            var current = (doc.GetElement(mat.ThermalAssetId) as PropertySetElement)?.GetThermalAsset();
            ThermalAsset asset;
            if (current == null) asset = new ThermalAsset(assetName, ThermalMaterialType.Solid);
            else
            {
                asset = current.Copy();
                asset.Name = assetName;
            }

            using (var tx = new Transaction(doc, "Plinth: Set material thermal asset"))
            {
                tx.Start();
                try
                {
                    plan.Apply(doc);
                    mat = (Material)plan.TargetFor(mat);
                    if (conductivity.HasValue) asset.ThermalConductivity = UnitUtils.ConvertToInternalUnits(conductivity.Value, UnitTypeId.WattsPerMeterKelvin);
                    if (specificHeat.HasValue) asset.SpecificHeat = UnitUtils.ConvertToInternalUnits(specificHeat.Value, UnitTypeId.JoulesPerKilogramDegreeCelsius);
                    if (emissivity.HasValue) asset.Emissivity = emissivity.Value;
                    if (permeability.HasValue) asset.Permeability = permeability.Value;
                    if (density.HasValue) asset.Density = UnitUtils.ConvertToInternalUnits(density.Value, UnitTypeId.KilogramsPerCubicMeter);
                    var set = PropertySetElement.Create(doc, asset);
                    mat.ThermalAssetId = set.Id;
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                    return CommandResult.Ok(new JObject
                    {
                        ["updated"] = true,
                        ["material_id"] = RevitCompat.GetId(mat.Id),
                        ["based_on_existing"] = current != null,
                        ["shared_edits"] = plan.Report(),
                        ["thermal_asset"] = GetMaterialPropertiesHandler.Thermal(set)
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("SET_FAILED: could not set the thermal asset: " + ex.Message);
                }
            }
        }
    }
}
