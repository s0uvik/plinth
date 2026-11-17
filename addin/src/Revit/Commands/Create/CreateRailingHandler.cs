// New in Plinth (PRD "New tools"): rvt-mcp cannot make railings. create_railing either puts railings on every side of
// a stair (Revit's own stair railing placement, on treads or on stringers) or draws one railing along an open
// polyline on a level, for balconies, terraces and stair wells. A stair that already has railings is refused with
// their ids, never given a second set. It needs only an ordinary transaction, so it also runs inside batch_execute
// (but not before create_stair has finished, which batch_execute cannot run).

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Create
{
    public sealed class CreateRailingHandler : IRevitCommand
    {
        public string Name => "create_railing";
        public string Description =>
            "Create railings either on a stair (stair_id, optional placement treads|stringer, default treads; the stair must have no "
            + "railings yet) or along an open path of [x,y] mm points (path, at least 2) on a level with optional base_offset_mm. "
            + "Optional type_name (default: the project's default stair railing type). Returns railing_ids and type_name.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""type_name"":{""type"":""string""},
            ""stair_id"":{""type"":""integer""},
            ""placement"":{""type"":""string"",""enum"":[""treads"",""stringer""]},
            ""path"":{""type"":""array"",""items"":{""type"":""array""},""description"":""Open polyline of [x,y] mm points""},
            ""level"":{""type"":""string""},
            ""base_offset_mm"":{""type"":""number""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var onStair = p["stair_id"] != null;
            var onPath = p["path"] != null;
            if (onStair == onPath)
                return CommandResult.Fail("INVALID_PARAMS: give either stair_id, or path with level, not both.");
            if (onStair && (p["level"] != null || p["base_offset_mm"] != null))
                return CommandResult.Fail("INVALID_PARAMS: level and base_offset_mm go with path; a stair's railings follow the stair.");
            if (onPath && p["placement"] != null)
                return CommandResult.Fail("INVALID_PARAMS: placement goes with stair_id; a path railing has no treads or stringers.");

            if (!Lookup.TryElementType<RailingType>(doc, null, p.Value<string>("type_name"), ElementTypeGroup.RailingsTypeForStairs, "railing",
                    out var railingType, out var failure)) return failure;

            return onStair ? OnStair(doc, p, railingType) : OnPath(app, doc, p, railingType);
        }

        private static CommandResult OnStair(Document doc, JObject p, RailingType railingType)
        {
            var stairId = p.Value<long>("stair_id");
            if (!Lookup.TryElement<Stairs>(doc, stairId, "stair", out var stairs, out var failure)) return failure;

            var existing = stairs.GetAssociatedRailings().Select(doc.GetElement).Where(e => e != null).ToList();
            if (existing.Count > 0)
                return CommandResult.Fail("RAILINGS_EXIST: stair " + stairId + " already has " + existing.Count
                    + " railing(s). Delete them first to replace them.", new
                    {
                        railings = existing.Select(e => new JObject
                        {
                            ["id"] = RevitCompat.GetId(e.Id),
                            ["type_name"] = doc.GetElement(e.GetTypeId())?.Name
                        }).ToList()
                    });
            if (!Railing.IsValidHostForNewRailing(doc, stairs.Id))
                return CommandResult.Fail("STAIR_NOT_RAILABLE: Revit cannot place railings on stair " + stairId
                    + " now (it may be in stair edit mode).");

            var position = p.Value<string>("placement") == "stringer" ? RailingPlacementPosition.Stringer : RailingPlacementPosition.Treads;
            return InTransaction(doc, () =>
            {
                var ids = Railing.Create(doc, stairs.Id, railingType.Id, position);
                if (ids == null || ids.Count == 0) return CommandResult.Fail("CREATE_FAILED: Revit created no railings on stair " + stairId + ".");
                return CommandResult.Ok(new JObject
                {
                    ["railing_ids"] = new JArray(ids.Select(RevitCompat.GetId)),
                    ["type_name"] = railingType.Name
                });
            });
        }

        private static CommandResult OnPath(UIApplication app, Document doc, JObject p, RailingType railingType)
        {
            var levelName = p.Value<string>("level");
            if (string.IsNullOrEmpty(levelName)) return CommandResult.Fail("INVALID_PARAMS: a path railing needs level.");
            if (!Lookup.TryLevel(doc, levelName, out var level, out var failure)) return failure;
            var baseOffsetMm = p.Value<double?>("base_offset_mm");
            if (baseOffsetMm.HasValue && !PlanPoints.IsFinite(baseOffsetMm.Value))
                return CommandResult.Fail("INVALID_PARAMS: base_offset_mm must be a finite number.");

            var shortCurve = app.Application.ShortCurveTolerance;
            if (!PlanPoints.TryList(p["path"], "path", 2, false, level.ProjectElevation, shortCurve, out var points, out failure)) return failure;
            if (points.Count > 2 && points[0].DistanceTo(points[points.Count - 1]) <= shortCurve)
                return CommandResult.Fail("INVALID_PATH: path must be open; its first and last points are the same.");

            var loop = new CurveLoop();
            for (var i = 0; i + 1 < points.Count; i++) loop.Append(Line.CreateBound(points[i], points[i + 1]));
            if (!Railing.IsValidPathForRailing(loop))
                return CommandResult.Fail("INVALID_PATH: Revit cannot use this path for a railing (it must not cross itself or "
                    + "fold back over an earlier segment).");

            return InTransaction(doc, () =>
            {
                var railing = Railing.Create(doc, loop, railingType.Id, level.Id);
                if (railing == null) return CommandResult.Fail("CREATE_FAILED: Revit did not create the railing.");
                if (baseOffsetMm.HasValue)
                {
                    var offset = railing.get_Parameter(BuiltInParameter.STAIRS_RAILING_HEIGHT_OFFSET);
                    if (offset == null || offset.IsReadOnly)
                        return CommandResult.Fail("CREATE_FAILED: the railing's base offset cannot be set. Nothing was changed.");
                    offset.Set(Mm.ToFeet(baseOffsetMm.Value));
                }
                return CommandResult.Ok(new JObject
                {
                    ["railing_ids"] = new JArray(RevitCompat.GetId(railing.Id)),
                    ["type_name"] = railingType.Name
                });
            });
        }

        private static CommandResult InTransaction(Document doc, Func<CommandResult> body)
        {
            using (var tx = new Transaction(doc, "Plinth: Create Railing"))
            {
                try
                {
                    tx.Start();
                    var result = body();
                    if (!result.Success)
                    {
                        if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                        return result;
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the railing back; see failures. Nothing was changed.");
                    return result;
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the railing: " + ex.Message + " Nothing was changed.");
                }
            }
        }
    }
}
