// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Mep/CreateCableTrayHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: level (by name) and cable tray type (by id or name) are required and a miss lists the options
// (rvt-mcp used the first CableTrayType and the level nearest start_z); a width or height the type cannot take
// rolls back instead of being ignored; hand-written param checks replaced by the schema; errors are "CODE: message".

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Mep
{
    public sealed class CreateCableTrayHandler : IRevitCommand
    {
        public string Name => "create_cable_tray";
        public string Description =>
            "Create a cable tray between two points (mm, internal coordinates; z is the absolute elevation). Required: level name and "
            + "cable tray type (cable_tray_type_id or cable_tray_type_name). Optional width and height in mm.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + MepHelpers.EndpointProperties + @",
            ""level"":{""type"":""string""},
            ""cable_tray_type_id"":{""type"":""integer""},
            ""cable_tray_type_name"":{""type"":""string""},
            ""width"":{""type"":""number""},
            ""height"":{""type"":""number""}},
            ""required"":[""start_x"",""start_y"",""start_z"",""end_x"",""end_y"",""end_z"",""level""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            if (!MepHelpers.TryEndpoints(app.Application, p, out var start, out var end, out var failure)) return failure;
            if (!Lookup.TryLevel(doc, p.Value<string>("level"), out var level, out failure)) return failure;
            if (!MepHelpers.TryType<CableTrayType>(doc, p.Value<long?>("cable_tray_type_id"), p.Value<string>("cable_tray_type_name"),
                    "cable tray", "cable_tray_type_id", "cable_tray_type_name", out var trayType, out failure)) return failure;
            var width = p.Value<double?>("width");
            var height = p.Value<double?>("height");

            using (var tx = new Transaction(doc, "Plinth: Create Cable Tray"))
            {
                tx.Start();
                try
                {
                    var tray = CableTray.Create(doc, trayType.Id, start, end, level.Id);
                    if (tray == null) throw new InvalidOperationException("Revit returned no cable tray.");
                    if (width.HasValue) MepHelpers.SetLengthMm(tray, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, width.Value, "width");
                    if (height.HasValue) MepHelpers.SetLengthMm(tray, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, height.Value, "height");

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    return CommandResult.Ok(new
                    {
                        cable_tray_id = RevitCompat.GetId(tray.Id),
                        type = trayType.Name,
                        type_id = RevitCompat.GetId(trayType.Id),
                        level = level.Name,
                        length_mm = MepHelpers.ReadLengthMm(tray, BuiltInParameter.CURVE_ELEM_LENGTH),
                        width_mm = MepHelpers.ReadLengthMm(tray, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM),
                        height_mm = MepHelpers.ReadLengthMm(tray, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM)
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the cable tray: " + ex.Message + " Nothing was created.");
                }
            }
        }
    }
}
