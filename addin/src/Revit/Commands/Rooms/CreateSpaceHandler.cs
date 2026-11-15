// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Rooms/CreateSpaceHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: without level_name the active plan's level is used and anything else is an error
// (rvt-mcp fell back to the first level); level and phase misses list valid options; the phase
// default (active view's phase, else the last phase) uses the ordered project phases and is reported;
// volume uses the exact ft3 to m3 factor.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Rooms
{
    public sealed class CreateSpaceHandler : IRevitCommand
    {
        public string Name => "create_space";
        public string Description =>
            "Place an MEP space at point x, y (mm) on level_name (default the active plan's level), in phase_name "
            + "(default the active view's phase, else the last phase). Optional name and number.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""x"":{""type"":""number""},
            ""y"":{""type"":""number""},
            ""level_name"":{""type"":""string""},
            ""phase_name"":{""type"":""string""},
            ""name"":{""type"":""string""},
            ""number"":{""type"":""string""}},""required"":[""x"",""y""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var xMm = p.Value<double>("x");
            var yMm = p.Value<double>("y");
            var levelName = p.Value<string>("level_name");
            var name = p.Value<string>("name");
            var number = p.Value<string>("number");

            Level level;
            CommandResult failure;
            if (!string.IsNullOrEmpty(levelName))
            {
                if (!Lookup.TryLevel(doc, levelName, out level, out failure)) return failure;
            }
            else
            {
                level = (app.ActiveUIDocument?.ActiveView as ViewPlan)?.GenLevel;
                if (level == null) return CommandResult.Fail("LEVEL_REQUIRED: the active view is not a plan with a level; pass level_name.");
            }
            if (!RoomsHelpers.TryPhase(app, doc, p.Value<string>("phase_name"), out var phase, out failure)) return failure;

            using (var tx = new Transaction(doc, "B2M: Create Space"))
            {
                tx.Start();
                try
                {
                    var space = doc.Create.NewSpace(level, phase, new UV(Mm.ToFeet(xMm), Mm.ToFeet(yMm)));
                    if (space == null) throw new InvalidOperationException("Revit returned no space.");
                    RoomsHelpers.SetNameAndNumber(space, name, number);
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures. Nothing was created.");

                    return CommandResult.Ok(new JObject
                    {
                        ["space"] = new JObject
                        {
                            ["element_id"] = RevitCompat.GetId(space.Id),
                            ["name"] = space.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? space.Name,
                            ["number"] = space.Number,
                            ["status"] = RoomsHelpers.StatusOf(space),
                            ["area_m2"] = RoomsHelpers.M2(space.Area),
                            ["volume_m3"] = RoomsHelpers.M3(space.Volume)
                        },
                        ["level"] = RoomsHelpers.Ref(level),
                        ["phase"] = RoomsHelpers.Ref(phase),
                        ["location"] = RoomsHelpers.Point((space.Location as LocationPoint)?.Point)
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create a space at (" + xMm + ", " + yMm + ") mm: " + ex.Message + " Nothing was created.");
                }
            }
        }
    }
}
