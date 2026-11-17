// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Mep/CreateConduitHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: level (by name) and conduit type (by id or name) are required and a miss lists the options
// (rvt-mcp used the first ConduitType and the level nearest start_z); a diameter the type cannot take rolls back
// instead of being ignored; errors are "CODE: message".

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Mep
{
    public sealed class CreateConduitHandler : IRevitCommand
    {
        public string Name => "create_conduit";
        public string Description =>
            "Create an electrical conduit between two points (mm, internal coordinates; z is the absolute elevation). Required: level name "
            + "and conduit type (conduit_type_id or conduit_type_name). Optional diameter in mm.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + MepHelpers.EndpointProperties + @",
            ""level"":{""type"":""string""},
            ""conduit_type_id"":{""type"":""integer""},
            ""conduit_type_name"":{""type"":""string""},
            ""diameter"":{""type"":""number""}},
            ""required"":[""start_x"",""start_y"",""start_z"",""end_x"",""end_y"",""end_z"",""level""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            if (!MepHelpers.TryEndpoints(app.Application, p, out var start, out var end, out var failure)) return failure;
            if (!Lookup.TryLevel(doc, p.Value<string>("level"), out var level, out failure)) return failure;
            if (!MepHelpers.TryType<ConduitType>(doc, p.Value<long?>("conduit_type_id"), p.Value<string>("conduit_type_name"),
                    "conduit", "conduit_type_id", "conduit_type_name", out var conduitType, out failure)) return failure;
            var diameter = p.Value<double?>("diameter");

            using (var tx = new Transaction(doc, "Plinth: Create Conduit"))
            {
                tx.Start();
                try
                {
                    var conduit = Conduit.Create(doc, conduitType.Id, start, end, level.Id);
                    if (conduit == null) throw new InvalidOperationException("Revit returned no conduit.");
                    if (diameter.HasValue) MepHelpers.SetLengthMm(conduit, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM, diameter.Value, "diameter");

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    return CommandResult.Ok(new
                    {
                        conduit_id = RevitCompat.GetId(conduit.Id),
                        type = conduitType.Name,
                        type_id = RevitCompat.GetId(conduitType.Id),
                        level = level.Name,
                        length_mm = MepHelpers.ReadLengthMm(conduit, BuiltInParameter.CURVE_ELEM_LENGTH),
                        diameter_mm = MepHelpers.ReadLengthMm(conduit, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the conduit: " + ex.Message + " Nothing was created.");
                }
            }
        }
    }
}
