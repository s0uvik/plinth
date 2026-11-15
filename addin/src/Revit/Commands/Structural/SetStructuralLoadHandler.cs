// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Structural/SetStructuralLoadHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: the action field and its "create" stub (which returned not_implemented) are dropped: the command
// updates; values are kN and kN*m converted to Revit's internal units (rvt-mcp wrote the raw numbers as internal units);
// only point loads are accepted, since LOAD_FORCE_* are point-load parameters (rvt-mcp silently changed nothing on line
// and area loads); a component that cannot be set rolls back instead of being skipped; the result reads the load back.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Structural
{
    public sealed class SetStructuralLoadHandler : IRevitCommand
    {
        private static readonly (string Field, BuiltInParameter Parameter, bool Moment)[] Components =
        {
            ("force_x", BuiltInParameter.LOAD_FORCE_FX, false),
            ("force_y", BuiltInParameter.LOAD_FORCE_FY, false),
            ("force_z", BuiltInParameter.LOAD_FORCE_FZ, false),
            ("moment_x", BuiltInParameter.LOAD_MOMENT_MX, true),
            ("moment_y", BuiltInParameter.LOAD_MOMENT_MY, true),
            ("moment_z", BuiltInParameter.LOAD_MOMENT_MZ, true),
        };

        public string Name => "set_structural_load";
        public string Description =>
            "Change the force (kN) and moment (kN*m) components of an existing point load (load_id). Give at least one of force_x, "
            + "force_y, force_z, moment_x, moment_y, moment_z; the others are kept. Line and area loads are not supported.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""load_id"":{""type"":""integer""},
            ""force_x"":{""type"":""number""},""force_y"":{""type"":""number""},""force_z"":{""type"":""number""},
            ""moment_x"":{""type"":""number""},""moment_y"":{""type"":""number""},""moment_z"":{""type"":""number""}},
            ""required"":[""load_id""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var loadId = p.Value<long>("load_id");
            var element = doc.GetElement(RevitCompat.ToElementId(loadId));
            if (element == null) return CommandResult.Fail("ELEMENT_NOT_FOUND: no element has id " + loadId + ".");
            if (element is LineLoad || element is AreaLoad)
                return CommandResult.Fail("UNSUPPORTED_LOAD_KIND: load " + loadId + " is a " + (element is LineLoad ? "line" : "area")
                    + " load; only point loads can be changed here.");
            if (!(element is PointLoad load)) return CommandResult.Fail("NOT_A_LOAD: element " + loadId + " is not a structural point load.");

            var changed = 0;
            using (var tx = new Transaction(doc, "B2M: Update Structural Load"))
            {
                tx.Start();
                try
                {
                    foreach (var c in Components)
                    {
                        var value = p.Value<double?>(c.Field);
                        if (!value.HasValue) continue;
                        var unit = c.Moment ? UnitTypeId.KilonewtonMeters : UnitTypeId.Kilonewtons;
                        StructuralHelpers.Set(load, c.Parameter, UnitUtils.ConvertToInternalUnits(value.Value, unit), c.Field);
                        changed++;
                    }
                    if (changed == 0)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("INVALID_PARAMS: give at least one of force_x, force_y, force_z, moment_x, moment_y, moment_z.");
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("UPDATE_FAILED: could not change the load: " + ex.Message + " Nothing changed.");
                }
            }

            var f = load.ForceVector;
            var m = load.MomentVector;
            return CommandResult.Ok(new
            {
                load_id = loadId,
                changed_fields = changed,
                force_kn = new[] { Kn(f.X, false), Kn(f.Y, false), Kn(f.Z, false) },
                moment_kn_m = new[] { Kn(m.X, true), Kn(m.Y, true), Kn(m.Z, true) }
            });
        }

        private static double Kn(double value, bool moment) =>
            Math.Round(UnitUtils.ConvertFromInternalUnits(value, moment ? UnitTypeId.KilonewtonMeters : UnitTypeId.Kilonewtons), 4);
    }
}
