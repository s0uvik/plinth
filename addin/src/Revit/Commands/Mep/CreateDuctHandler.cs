// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Mep/CreateDuctHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: level (by name), duct type and system type (by id or name) are required and a miss lists
// the options (rvt-mcp used the first DuctType, the first MechanicalSystemType and the level nearest start_z);
// a requested size that the type cannot take is an error that rolls back (rvt-mcp ignored it silently);
// diameter together with width/height is refused; the response reads size and length back from Revit.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Mep
{
    public sealed class CreateDuctHandler : IRevitCommand
    {
        public string Name => "create_duct";
        public string Description =>
            "Create an HVAC duct between two points (mm, internal coordinates; z is the absolute elevation). Required: level name, "
            + "duct type (duct_type_id or duct_type_name) and mechanical system type (system_type_id or system_type_name). "
            + "Optional size: diameter for round ducts, or width and height for rectangular ducts (mm).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + MepHelpers.EndpointProperties + @",
            ""level"":{""type"":""string""},
            ""duct_type_id"":{""type"":""integer""},
            ""duct_type_name"":{""type"":""string""},
            ""system_type_id"":{""type"":""integer""},
            ""system_type_name"":{""type"":""string""},
            ""diameter"":{""type"":""number""},
            ""width"":{""type"":""number""},
            ""height"":{""type"":""number""}},
            ""required"":[""start_x"",""start_y"",""start_z"",""end_x"",""end_y"",""end_z"",""level""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var diameter = p.Value<double?>("diameter");
            var width = p.Value<double?>("width");
            var height = p.Value<double?>("height");
            if (diameter.HasValue && (width.HasValue || height.HasValue))
                return CommandResult.Fail("INVALID_PARAMS: give diameter (round) or width and height (rectangular), not both.");

            if (!MepHelpers.TryEndpoints(app.Application, p, out var start, out var end, out var failure)) return failure;
            if (!Lookup.TryLevel(doc, p.Value<string>("level"), out var level, out failure)) return failure;
            if (!MepHelpers.TryType<DuctType>(doc, p.Value<long?>("duct_type_id"), p.Value<string>("duct_type_name"), "duct",
                    "duct_type_id", "duct_type_name", out var ductType, out failure)) return failure;
            if (!MepHelpers.TryType<MechanicalSystemType>(doc, p.Value<long?>("system_type_id"), p.Value<string>("system_type_name"),
                    "mechanical system", "system_type_id", "system_type_name", out var systemType, out failure)) return failure;

            using (var tx = new Transaction(doc, "B2M: Create Duct"))
            {
                tx.Start();
                try
                {
                    var duct = Duct.Create(doc, systemType.Id, ductType.Id, level.Id, start, end);
                    if (duct == null) throw new InvalidOperationException("Revit returned no duct.");
                    if (diameter.HasValue) MepHelpers.SetLengthMm(duct, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, diameter.Value, "diameter");
                    if (width.HasValue) MepHelpers.SetLengthMm(duct, BuiltInParameter.RBS_CURVE_WIDTH_PARAM, width.Value, "width");
                    if (height.HasValue) MepHelpers.SetLengthMm(duct, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, height.Value, "height");

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    return CommandResult.Ok(new
                    {
                        duct_id = RevitCompat.GetId(duct.Id),
                        duct_type = ductType.Name,
                        duct_type_id = RevitCompat.GetId(ductType.Id),
                        system_type = systemType.Name,
                        system_type_id = RevitCompat.GetId(systemType.Id),
                        level = level.Name,
                        length_mm = MepHelpers.ReadLengthMm(duct, BuiltInParameter.CURVE_ELEM_LENGTH),
                        diameter_mm = MepHelpers.ReadLengthMm(duct, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM),
                        width_mm = MepHelpers.ReadLengthMm(duct, BuiltInParameter.RBS_CURVE_WIDTH_PARAM),
                        height_mm = MepHelpers.ReadLengthMm(duct, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the duct: " + ex.Message + " Nothing was created.");
                }
            }
        }
    }
}
