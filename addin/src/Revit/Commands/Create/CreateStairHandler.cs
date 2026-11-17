// New in Plinth (PRD "New tools"): rvt-mcp cannot make stairs. create_stair builds a component stair from 1 to 4
// straight runs inside a StairsEditScope, joining consecutive runs with Revit's automatic landings.
// Each run is given by its first-riser point on the centreline, a direction and a riser count; the add-in works out
// the run's length from the riser count and the tread depth and returns where each run really ends. The stair
// instance gets the exact riser count and tread depth, so Revit has nothing to warn about; a stair type limit
// (maximum riser height, minimum tread depth, minimum run width) that the request would break is an error naming
// the limit and the type, and the shared type is never changed.
// StairsEditScope cannot start while a transaction is open and cannot close while a transaction group is open, so
// create_stair is refused inside batch_execute (BatchExecutor) and must be called directly.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Create
{
    public sealed class CreateStairHandler : IRevitCommand
    {
        private const int MaxRuns = 4;
        private static readonly double LimitTolerance = Mm.ToFeet(0.01);

        public string Name => "create_stair";
        public string Description =>
            "Create a stair from base_level to top_level with 1-4 straight runs, each {start:[x,y] mm (centreline at the first riser), "
            + "end:[x,y] mm (direction only), risers}. Consecutive runs are joined by automatic landings. width_mm is the run width, "
            + "tread_mm the tread depth; riser height = (top - base - base_offset_mm) / total risers. Optional type_name "
            + "(default: the project's default stair type). Returns the actual run ends. Call directly, not inside batch_execute.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""base_level"":{""type"":""string""},
            ""top_level"":{""type"":""string""},
            ""type_name"":{""type"":""string""},
            ""width_mm"":{""type"":""number""},
            ""tread_mm"":{""type"":""number""},
            ""base_offset_mm"":{""type"":""number""},
            ""runs"":{""type"":""array"",""items"":{""type"":""object""},""description"":""1-4 runs of {start:[x,y], end:[x,y], risers}""}},
            ""required"":[""base_level"",""top_level"",""width_mm"",""tread_mm"",""runs""]}";
        public bool ReadOnly => false;

        private sealed class RunSpec
        {
            public XYZ Start;
            public XYZ Direction;
            public int Risers;
            public XYZ ActualEnd;
        }

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (doc.IsFamilyDocument) return CommandResult.Fail("NOT_A_PROJECT: stairs can only be created in a project document.");

            if (!Lookup.TryLevel(doc, p.Value<string>("base_level"), out var baseLevel, out var failure)) return failure;
            if (!Lookup.TryLevel(doc, p.Value<string>("top_level"), out var topLevel, out failure)) return failure;
            if (!Lookup.TryElementType<StairsType>(doc, null, p.Value<string>("type_name"), ElementTypeGroup.StairsType, "stair",
                    out var stairsType, out failure)) return failure;

            var widthMm = p.Value<double>("width_mm");
            var treadMm = p.Value<double>("tread_mm");
            var baseOffsetMm = p.Value<double?>("base_offset_mm") ?? 0;
            if (!PlanPoints.IsFinite(widthMm) || widthMm <= 0) return CommandResult.Fail("INVALID_PARAMS: width_mm must be more than 0.");
            if (!PlanPoints.IsFinite(treadMm) || treadMm <= 0) return CommandResult.Fail("INVALID_PARAMS: tread_mm must be more than 0.");
            if (!PlanPoints.IsFinite(baseOffsetMm)) return CommandResult.Fail("INVALID_PARAMS: base_offset_mm must be a finite number.");
            var width = Mm.ToFeet(widthMm);
            var tread = Mm.ToFeet(treadMm);
            var baseOffset = Mm.ToFeet(baseOffsetMm);

            if (!TryRuns(p["runs"] as JArray, app.Application.ShortCurveTolerance, out var runs, out failure)) return failure;
            var totalRisers = runs.Sum(r => r.Risers);

            var baseZ = baseLevel.ProjectElevation + baseOffset;
            var height = topLevel.ProjectElevation - baseZ;
            if (height <= LimitTolerance)
                return CommandResult.Fail("INVALID_HEIGHT: top_level " + topLevel.Name + " must be above base_level " + baseLevel.Name
                    + " plus base_offset_mm.", new { heightMm = Mm.FromFeet(height) });
            var riser = height / totalRisers;

            if (!CheckTypeLimits(stairsType, height, totalRisers, riser, tread, width, out failure)) return failure;

            return Build(doc, baseLevel, topLevel, stairsType, runs, totalRisers, riser, tread, width, baseOffset, baseZ);
        }

        private static bool TryRuns(JArray array, double shortCurve, out List<RunSpec> runs, out CommandResult failure)
        {
            runs = new List<RunSpec>();
            failure = null;
            if (array == null || array.Count < 1 || array.Count > MaxRuns)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: runs needs 1 to " + MaxRuns + " items of {start:[x,y], end:[x,y], risers}.");
                return false;
            }
            for (var i = 0; i < array.Count; i++)
            {
                var run = array[i] as JObject;
                if (run == null)
                {
                    failure = CommandResult.Fail("INVALID_PARAMS: runs[" + i + "] must be {start:[x,y], end:[x,y], risers}.");
                    return false;
                }
                var unknown = run.Properties().Select(prop => prop.Name).Where(n => n != "start" && n != "end" && n != "risers").ToList();
                if (unknown.Count > 0)
                {
                    failure = CommandResult.Fail("INVALID_PARAMS: runs[" + i + "] has unknown field '" + unknown[0] + "'. Valid fields: start, end, risers.");
                    return false;
                }
                if (!PlanPoints.TryPoint(run["start"], "runs[" + i + "].start", 0, out var start, out failure)) return false;
                if (!PlanPoints.TryPoint(run["end"], "runs[" + i + "].end", 0, out var end, out failure)) return false;
                var risersToken = run["risers"];
                if (risersToken == null || risersToken.Type != JTokenType.Integer || risersToken.Value<long>() < 2 || risersToken.Value<long>() > 1000)
                {
                    failure = CommandResult.Fail("INVALID_PARAMS: runs[" + i + "].risers must be a whole number of at least 2 "
                        + "(a straight run of n risers has n - 1 treads).");
                    return false;
                }
                var direction = end - start;
                if (direction.GetLength() <= shortCurve)
                {
                    failure = CommandResult.Fail("INVALID_PARAMS: runs[" + i + "].start and end are the same point; end gives the run's direction.");
                    return false;
                }
                runs.Add(new RunSpec { Start = start, Direction = direction.Normalize(), Risers = risersToken.Value<int>() });
            }
            return true;
        }

        private static bool CheckTypeLimits(StairsType type, double height, int totalRisers, double riser, double tread, double width,
            out CommandResult failure)
        {
            failure = null;
            if (riser > type.MaxRiserHeight + LimitTolerance)
            {
                var minimum = (int)Math.Ceiling(height / type.MaxRiserHeight - 1e-9);
                failure = CommandResult.Fail("TYPE_LIMIT: the riser height would be " + Mm.FromFeet(riser, 2) + " mm, above the maximum riser height "
                    + Mm.FromFeet(type.MaxRiserHeight, 2) + " mm of stair type '" + type.Name + "'. Use at least " + minimum
                    + " risers in total, or another stair type.", new
                    {
                        limit = "maximum_riser_height",
                        type_name = type.Name,
                        limit_mm = Mm.FromFeet(type.MaxRiserHeight, 2),
                        riser_height_mm = Mm.FromFeet(riser, 2),
                        total_risers = totalRisers,
                        min_total_risers = minimum
                    });
                return false;
            }
            if (tread < type.MinTreadDepth - LimitTolerance)
            {
                failure = CommandResult.Fail("TYPE_LIMIT: tread_mm " + Mm.FromFeet(tread, 2) + " is below the minimum tread depth "
                    + Mm.FromFeet(type.MinTreadDepth, 2) + " mm of stair type '" + type.Name + "'.", new
                    {
                        limit = "minimum_tread_depth",
                        type_name = type.Name,
                        limit_mm = Mm.FromFeet(type.MinTreadDepth, 2),
                        tread_mm = Mm.FromFeet(tread, 2)
                    });
                return false;
            }
            if (width < type.MinRunWidth - LimitTolerance)
            {
                failure = CommandResult.Fail("TYPE_LIMIT: width_mm " + Mm.FromFeet(width, 2) + " is below the minimum run width "
                    + Mm.FromFeet(type.MinRunWidth, 2) + " mm of stair type '" + type.Name + "'.", new
                    {
                        limit = "minimum_run_width",
                        type_name = type.Name,
                        limit_mm = Mm.FromFeet(type.MinRunWidth, 2),
                        width_mm = Mm.FromFeet(width, 2)
                    });
                return false;
            }
            return true;
        }

        private static CommandResult Build(Document doc, Level baseLevel, Level topLevel, StairsType stairsType, List<RunSpec> runs,
            int totalRisers, double riser, double tread, double width, double baseOffset, double baseZ)
        {
            using (var scope = new StairsEditScope(doc, "Plinth: Create Stair"))
            {
                if (!scope.IsPermitted)
                    return CommandResult.Fail("STAIRS_EDIT_NOT_PERMITTED: Revit cannot start stair editing now (a transaction or another edit "
                        + "mode is open). Call create_stair on its own, not inside batch_execute.");

                ElementId stairsId;
                try
                {
                    stairsId = scope.Start(baseLevel.Id, topLevel.Id);
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail("CREATE_FAILED: could not start stair editing: " + ex.Message);
                }

                var runIds = new List<ElementId>();
                var landingIds = new List<ElementId>();
                var failure = BuildRunsAndLandings(doc, stairsId, stairsType, runs, totalRisers, riser, tread, width, baseOffset, baseZ,
                    runIds, landingIds);
                if (failure != null)
                {
                    if (scope.IsActive) scope.Cancel();
                    return failure;
                }

                try
                {
                    // Commit returns nothing; a rollback by the FailureScope shows as the stair being gone below.
                    scope.Commit(new LeaveToFailureScope());
                }
                catch (Exception ex)
                {
                    if (scope.IsActive) scope.Cancel();
                    return CommandResult.Fail("CREATE_FAILED: could not finish the stair: " + ex.Message + " Nothing was changed.");
                }

                if (!(doc.GetElement(stairsId) is Stairs stairs))
                    return CommandResult.Fail("NOT_COMMITTED: Revit rolled the stair back; see failures. Nothing was changed.");
                return CommandResult.Ok(new JObject
                {
                    ["stair_id"] = RevitCompat.GetId(stairs.Id),
                    ["run_ids"] = new JArray(runIds.Select(RevitCompat.GetId)),
                    ["landing_ids"] = new JArray(landingIds.Select(RevitCompat.GetId)),
                    ["type_name"] = stairsType.Name,
                    ["risers"] = stairs.ActualRisersNumber,
                    ["riser_height_mm"] = Mm.FromFeet(stairs.ActualRiserHeight, 2),
                    ["tread_mm"] = Mm.FromFeet(stairs.ActualTreadDepth, 2),
                    ["runs"] = new JArray(runs.Select(r => new JObject
                    {
                        ["start"] = PlanPoints.ToJson(r.Start),
                        ["end"] = PlanPoints.ToJson(r.ActualEnd)
                    })),
                    // Railings Revit may have attached on its own; create_railing refuses a stair that already has some.
                    ["railing_ids"] = new JArray(stairs.GetAssociatedRailings().Select(RevitCompat.GetId))
                });
            }
        }

        /// <summary>Runs inside the open edit scope. Returns null on success, else the failure (the transaction is rolled back).</summary>
        private static CommandResult BuildRunsAndLandings(Document doc, ElementId stairsId, StairsType stairsType, List<RunSpec> runs,
            int totalRisers, double riser, double tread, double width, double baseOffset, double baseZ,
            List<ElementId> runIds, List<ElementId> landingIds)
        {
            using (var tx = new Transaction(doc, "Plinth: Stair runs"))
            {
                tx.Start();
                try
                {
                    var stairs = (Stairs)doc.GetElement(stairsId);
                    if (stairs.GetTypeId() != stairsType.Id) stairs.ChangeTypeId(stairsType.Id);

                    var offsetParam = stairs.get_Parameter(BuiltInParameter.STAIRS_BASE_OFFSET);
                    if (offsetParam == null || offsetParam.IsReadOnly)
                        return RolledBack(tx, "CREATE_FAILED: the stair's base offset cannot be set.");
                    offsetParam.Set(baseOffset);
                    var topOffsetParam = stairs.get_Parameter(BuiltInParameter.STAIRS_TOP_OFFSET);
                    if (topOffsetParam != null && !topOffsetParam.IsReadOnly) topOffsetParam.Set(0.0);
                    stairs.DesiredRisersNumber = totalRisers;
                    stairs.ActualTreadDepth = tread;
                    doc.Regenerate();
                    if (Math.Abs(stairs.ActualRiserHeight - riser) > LimitTolerance)
                        return RolledBack(tx, "CREATE_FAILED: Revit set the riser height to " + Mm.FromFeet(stairs.ActualRiserHeight, 2)
                            + " mm, expected " + Mm.FromFeet(riser, 2) + " mm.");

                    var created = new List<StairsRun>();
                    var climbed = 0;
                    for (var i = 0; i < runs.Count; i++)
                    {
                        var spec = runs[i];
                        var start = new XYZ(spec.Start.X, spec.Start.Y, baseZ + climbed * riser);
                        // A straight run that begins and ends with a riser has risers - 1 treads along its path.
                        var length = (spec.Risers - 1) * tread;
                        var run = StairsRun.CreateStraightRun(doc, stairsId, Line.CreateBound(start, start + spec.Direction * length),
                            StairsRunJustification.Center);
                        if (run.ActualRisersNumber != spec.Risers)
                        {
                            // Revit rounds the path to whole treads; if its rule differs from the one above, lengthen or
                            // shorten the path by the missing treads once, then check again.
                            length += (spec.Risers - run.ActualRisersNumber) * tread;
                            if (length > 0)
                            {
                                StairsRun.SetLocationPathForStraightRun(run, Line.CreateBound(start, start + spec.Direction * length));
                                doc.Regenerate();
                            }
                        }
                        if (run.ActualRisersNumber != spec.Risers)
                            return RolledBack(tx, "RUN_RISERS_MISMATCH: run " + i + " got " + run.ActualRisersNumber + " risers, "
                                + spec.Risers + " were asked for. Nothing was changed.", new { run = i, requested = spec.Risers, actual = run.ActualRisersNumber });
                        run.ActualRunWidth = width;
                        spec.ActualEnd = start + spec.Direction * length;
                        created.Add(run);
                        runIds.Add(run.Id);
                        climbed += spec.Risers;
                    }

                    for (var i = 0; i + 1 < created.Count; i++)
                    {
                        if (!StairsLanding.CanCreateAutomaticLanding(doc, created[i].Id, created[i + 1].Id))
                            return RolledBack(tx, "RUNS_NOT_CONNECTABLE: Revit cannot make an automatic landing between run " + i + " and run "
                                + (i + 1) + ". Run " + (i + 1) + " must start ahead of or beside run " + i + "'s actual end (straight, L or U "
                                + "layout) with room for a landing. Nothing was changed.", new
                                {
                                    run = i,
                                    end = PlanPoints.ToJson(runs[i].ActualEnd),
                                    next_start = PlanPoints.ToJson(runs[i + 1].Start)
                                });
                        landingIds.AddRange(StairsLanding.CreateAutomaticLanding(doc, created[i].Id, created[i + 1].Id));
                    }

                    doc.Regenerate();
                    if (stairs.ActualRisersNumber != totalRisers)
                        return RolledBack(tx, "STAIR_RISERS_MISMATCH: the stair has " + stairs.ActualRisersNumber + " risers, "
                            + totalRisers + " were asked for. Nothing was changed.");

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the stair back; see failures. Nothing was changed.");
                    return null;
                }
                catch (Exception ex)
                {
                    return RolledBack(tx, "CREATE_FAILED: could not create the stair: " + ex.Message + " Nothing was changed.");
                }
            }
        }

        private static CommandResult RolledBack(Transaction tx, string error, object data = null)
        {
            if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
            return CommandResult.Fail(error, data);
        }

        /// <summary>The edit scope needs a preprocessor; the request's FailureScope decides (any warning rolls back).</summary>
        private sealed class LeaveToFailureScope : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor) => FailureProcessingResult.Continue;
        }
    }
}
