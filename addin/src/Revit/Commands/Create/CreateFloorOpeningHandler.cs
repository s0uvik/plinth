using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Create
{
    /// <summary>
    /// New in B2M (rvt-mcp has no opening tool). Cuts a hole from an outline of {x, y} points in mm:
    /// with floorId, through that one floor; with baseLevel and topLevel, a shaft through every floor
    /// between them, which is how the stair core is cut.
    /// </summary>
    public sealed class CreateFloorOpeningHandler : IRevitCommand
    {
        public string Name => "create_floor_opening";
        public string Description =>
            "Cut an opening from an outline of {x, y} points in mm: through one floor (floorId), or a shaft through all floors "
            + "between baseLevel and topLevel (optional baseOffset, topOffset in mm).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""points"":{""type"":""array"",""items"":{""type"":""object""}},
            ""floorId"":{""type"":""integer""},
            ""baseLevel"":{""type"":""string""},
            ""topLevel"":{""type"":""string""},
            ""baseOffset"":{""type"":""number""},
            ""topOffset"":{""type"":""number""}},""required"":[""points""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var floorId = p.Value<long?>("floorId");
            var baseName = p.Value<string>("baseLevel");
            var topName = p.Value<string>("topLevel");
            var isShaft = !string.IsNullOrEmpty(baseName) || !string.IsNullOrEmpty(topName);
            if (floorId.HasValue == isShaft)
                return CommandResult.Fail("INVALID_PARAMS: give floorId for one floor, or baseLevel and topLevel for a shaft.");

            Floor floor = null;
            Level baseLevel = null, topLevel = null;
            CommandResult failure;
            double z;
            if (floorId.HasValue)
            {
                if (!Lookup.TryElement(doc, floorId.Value, "floor", out floor, out failure)) return failure;
                z = (doc.GetElement(floor.LevelId) as Level)?.ProjectElevation ?? 0;
            }
            else
            {
                if (string.IsNullOrEmpty(baseName) || string.IsNullOrEmpty(topName))
                    return CommandResult.Fail("INVALID_PARAMS: a shaft needs both baseLevel and topLevel.");
                if (!Lookup.TryLevel(doc, baseName, out baseLevel, out failure)) return failure;
                if (!Lookup.TryLevel(doc, topName, out topLevel, out failure)) return failure;
                var baseZ = baseLevel.ProjectElevation + Mm.ToFeet(p.Value<double?>("baseOffset") ?? 0);
                var topZ = topLevel.ProjectElevation + Mm.ToFeet(p.Value<double?>("topOffset") ?? 0);
                if (topZ <= baseZ) return CommandResult.Fail("INVALID_HEIGHT: the shaft top must be above its base.");
                z = baseLevel.ProjectElevation;
            }
            if (!Outline.TryCurveArray((JArray)p["points"], z, app.Application.ShortCurveTolerance, out var profile, out failure)) return failure;

            using (var tx = new Transaction(doc, isShaft ? "B2M: Create Shaft Opening" : "B2M: Create Floor Opening"))
            {
                tx.Start();
                try
                {
                    Opening opening;
                    if (isShaft)
                    {
                        opening = doc.Create.NewOpening(baseLevel, topLevel, profile);
                        opening.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.Set(Mm.ToFeet(p.Value<double?>("baseOffset") ?? 0));
                        opening.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.Set(Mm.ToFeet(p.Value<double?>("topOffset") ?? 0));
                    }
                    else
                    {
                        opening = doc.Create.NewOpening(floor, profile, true);
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                    return CommandResult.Ok(new
                    {
                        elementId = RevitCompat.GetId(opening.Id),
                        kind = isShaft ? "shaft" : "floor",
                        floorId,
                        baseLevel = baseLevel?.Name,
                        topLevel = topLevel?.Name
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the opening: " + ex.Message);
                }
            }
        }
    }
}
