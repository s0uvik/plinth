// New in B2M (PRD "New tools"): rvt-mcp cannot make roofs. create_roof makes a footprint roof from a closed plan
// polygon on a level: flat by default, or sloped at slope_deg on every edge or on the chosen edges (edge i runs
// from point i to point i + 1), which covers flat terraces, mono-pitch, gable and hip roofs.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Create
{
    public sealed class CreateRoofHandler : IRevitCommand
    {
        private static readonly double MatchTolerance = Mm.ToFeet(0.5);

        public string Name => "create_roof";
        public string Description =>
            "Create a footprint roof on a level from a closed polygon of [x,y] mm points (3 or more, first point not repeated). "
            + "slope_deg (default 0 = flat) applies to sloped_edges (edge i runs from point i to point i+1; default every edge when "
            + "slope_deg > 0). Optional type_name (default: the project's default roof type) and base_offset_mm.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""level"":{""type"":""string""},
            ""footprint"":{""type"":""array"",""items"":{""type"":""array""},""description"":""Closed polygon of [x,y] mm points""},
            ""type_name"":{""type"":""string""},
            ""slope_deg"":{""type"":""number""},
            ""sloped_edges"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""base_offset_mm"":{""type"":""number""}},
            ""required"":[""level"",""footprint""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            if (!Lookup.TryLevel(doc, p.Value<string>("level"), out var level, out var failure)) return failure;
            if (!Lookup.TryElementType<RoofType>(doc, null, p.Value<string>("type_name"), ElementTypeGroup.RoofType, "roof",
                    out var roofType, out failure)) return failure;
            if (!PlanPoints.TryList(p["footprint"], "footprint", 3, true, level.ProjectElevation, app.Application.ShortCurveTolerance,
                    out var points, out failure)) return failure;

            var slopeDeg = p.Value<double?>("slope_deg") ?? 0;
            if (!PlanPoints.IsFinite(slopeDeg) || slopeDeg < 0 || slopeDeg >= 90)
                return CommandResult.Fail("INVALID_PARAMS: slope_deg must be at least 0 and less than 90.");
            var baseOffsetMm = p.Value<double?>("base_offset_mm");
            if (baseOffsetMm.HasValue && !PlanPoints.IsFinite(baseOffsetMm.Value))
                return CommandResult.Fail("INVALID_PARAMS: base_offset_mm must be a finite number.");

            var edgeCount = points.Count;
            SortedSet<int> sloped;
            if (p["sloped_edges"] != null)
            {
                var given = p["sloped_edges"].ToObject<long[]>();
                var bad = given.Where(i => i < 0 || i >= edgeCount).ToList();
                if (bad.Count > 0)
                    return CommandResult.Fail("INVALID_PARAMS: sloped_edges must be edge indices 0 to " + (edgeCount - 1)
                        + " (edge i runs from footprint point i to point i+1).", new { invalid = bad });
                if (given.Distinct().Count() != given.Length)
                    return CommandResult.Fail("INVALID_PARAMS: sloped_edges lists an edge more than once.");
                if (slopeDeg == 0 && given.Length > 0)
                    return CommandResult.Fail("INVALID_PARAMS: sloped_edges needs slope_deg above 0.");
                sloped = new SortedSet<int>(given.Select(i => (int)i));
            }
            else
            {
                sloped = new SortedSet<int>(slopeDeg > 0 ? Enumerable.Range(0, edgeCount) : Enumerable.Empty<int>());
            }

            var footprint = new CurveArray();
            for (var i = 0; i < edgeCount; i++) footprint.Append(Line.CreateBound(points[i], points[(i + 1) % edgeCount]));

            using (var tx = new Transaction(doc, "B2M: Create Roof"))
            {
                tx.Start();
                try
                {
                    var roof = doc.Create.NewFootPrintRoof(footprint, level, roofType, out var modelCurves);
                    if (roof == null) return RolledBack(tx, "CREATE_FAILED: Revit did not create the roof.");

                    // Match each sketch line Revit made to the footprint edge it came from by its end points, rather
                    // than trusting the order of the mapping array.
                    var matched = new HashSet<int>();
                    foreach (ModelCurve modelCurve in modelCurves)
                    {
                        var edge = EdgeIndex(modelCurve.GeometryCurve, points);
                        if (edge < 0 || !matched.Add(edge))
                            return RolledBack(tx, "CREATE_FAILED: could not match the roof's sketch lines to the footprint edges. Nothing was changed.");
                        var slopes = sloped.Contains(edge);
                        roof.set_DefinesSlope(modelCurve, slopes);
                        if (slopes) roof.set_SlopeAngle(modelCurve, Math.Tan(slopeDeg * Math.PI / 180.0));
                    }
                    if (matched.Count != edgeCount)
                        return RolledBack(tx, "CREATE_FAILED: Revit made " + matched.Count + " roof sketch lines for " + edgeCount
                            + " footprint edges. Nothing was changed.");

                    if (baseOffsetMm.HasValue)
                    {
                        var offset = roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM);
                        if (offset == null || offset.IsReadOnly)
                            return RolledBack(tx, "CREATE_FAILED: the roof's base offset cannot be set. Nothing was changed.");
                        offset.Set(Mm.ToFeet(baseOffsetMm.Value));
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the roof back; see failures. Nothing was changed.");
                    return CommandResult.Ok(new JObject
                    {
                        ["roof_id"] = RevitCompat.GetId(roof.Id),
                        ["type_name"] = roofType.Name,
                        ["slope_deg"] = slopeDeg,
                        ["sloped_edges"] = new JArray(sloped)
                    });
                }
                catch (Exception ex)
                {
                    return RolledBack(tx, "CREATE_FAILED: could not create the roof: " + ex.Message + " Nothing was changed.");
                }
            }
        }

        private static int EdgeIndex(Curve curve, List<XYZ> points)
        {
            if (curve == null || !curve.IsBound) return -1;
            var a = curve.GetEndPoint(0);
            var b = curve.GetEndPoint(1);
            for (var i = 0; i < points.Count; i++)
            {
                var p = points[i];
                var q = points[(i + 1) % points.Count];
                if ((PlanPoints.SamePlanPoint(a, p, MatchTolerance) && PlanPoints.SamePlanPoint(b, q, MatchTolerance))
                    || (PlanPoints.SamePlanPoint(a, q, MatchTolerance) && PlanPoints.SamePlanPoint(b, p, MatchTolerance)))
                    return i;
            }
            return -1;
        }

        private static CommandResult RolledBack(Transaction tx, string error)
        {
            if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
            return CommandResult.Fail(error);
        }
    }
}
