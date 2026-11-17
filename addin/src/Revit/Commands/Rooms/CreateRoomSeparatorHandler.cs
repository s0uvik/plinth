// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Rooms/CreateRoomSeparatorHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: points are {x, y} in mm and lie on the level (the per-point z is dropped: room
// separation lines must lie in the level's sketch plane); repeated or too-close points are an error
// instead of being dropped silently; at most 1000 points; the sketch plane is the level's own; a
// view_id must be a floor/area plan of the level given; without view_id the active plan is used only
// when it is on the level, else the level's first floor plan, else (a level with no plan yet, as during a build)
// a temporary floor plan deleted again in the same transaction; the response names the view used.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Rooms
{
    public sealed class CreateRoomSeparatorHandler : IRevitCommand
    {
        private const int MaxPoints = 1000;

        public string Name => "create_room_separator";
        public string Description =>
            "Draw room separation lines through points [{x, y}] in mm on a level, in a plan view (view_id, else the active plan, "
            + "else the first floor plan of level_name). close_loop joins the last point to the first.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""points"":{""type"":""array"",""items"":{""type"":""object""}},
            ""view_id"":{""type"":""integer""},
            ""level_name"":{""type"":""string""},
            ""close_loop"":{""type"":""boolean""}},""required"":[""points""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var points = (JArray)p["points"];
            var closeLoop = p.Value<bool?>("close_loop") ?? false;
            var viewId = p.Value<long?>("view_id");
            var levelName = p.Value<string>("level_name");
            if (points.Count < 2 || points.Count > MaxPoints)
                return CommandResult.Fail("INVALID_POINTS: give 2 to " + MaxPoints + " points, each {\"x\": mm, \"y\": mm}.");
            if (closeLoop && points.Count < 3)
                return CommandResult.Fail("INVALID_POINTS: close_loop needs at least 3 points.");

            Level level = null;
            CommandResult failure;
            if (!string.IsNullOrEmpty(levelName) && !Lookup.TryLevel(doc, levelName, out level, out failure)) return failure;

            ViewPlan plan;
            if (viewId.HasValue)
            {
                if (!Lookup.TryView(doc, viewId.Value, out var view, out failure)) return failure;
                plan = view as ViewPlan;
                if (plan == null || (plan.ViewType != ViewType.FloorPlan && plan.ViewType != ViewType.AreaPlan && plan.ViewType != ViewType.CeilingPlan))
                    return CommandResult.Fail("NOT_A_PLAN_VIEW: view " + viewId.Value + " ('" + view.Name + "') is a " + view.ViewType + "; room separators need a plan view.");
                if (level != null && (plan.GenLevel == null || plan.GenLevel.Id != level.Id))
                    return CommandResult.Fail("LEVEL_MISMATCH: view '" + plan.Name + "' is on level '" + plan.GenLevel?.Name + "', not '" + level.Name + "'.");
            }
            else
            {
                plan = app.ActiveUIDocument?.ActiveView as ViewPlan;
                if (plan != null && (plan.GenLevel == null || (level != null && plan.GenLevel.Id != level.Id))) plan = null;
                if (plan == null && level != null)
                    plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                        .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel != null && v.GenLevel.Id == level.Id)
                        .OrderBy(v => v.Name).FirstOrDefault();
                if (plan == null && level == null)
                    return CommandResult.Fail("NO_PLAN_VIEW: the active view is not a plan; pass view_id or level_name.");
            }
            // A level with no floor plan yet (a build makes its levels without views): the lines are drawn through a
            // temporary floor plan of the level, deleted again in the same transaction; separation lines are model
            // lines on the level's sketch plane and stay without it.
            var temporaryView = plan == null;
            level = level ?? plan.GenLevel;
            if (level == null) return CommandResult.Fail("NO_LEVEL: view '" + plan.Name + "' has no associated level; pass level_name.");

            if (!TryPoints(points, level.ProjectElevation, app.Application.ShortCurveTolerance, closeLoop, out var xyz, out failure)) return failure;

            var curves = new CurveArray();
            double lengthFeet = 0;
            var segments = closeLoop ? xyz.Count : xyz.Count - 1;
            for (var i = 0; i < segments; i++)
            {
                var line = Line.CreateBound(xyz[i], xyz[(i + 1) % xyz.Count]);
                curves.Append(line);
                lengthFeet += line.Length;
            }

            using (var tx = new Transaction(doc, "Plinth: Create Room Separator"))
            {
                tx.Start();
                try
                {
                    if (temporaryView)
                    {
                        var planType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                            .FirstOrDefault(t => t.ViewFamily == ViewFamily.FloorPlan);
                        if (planType == null)
                        {
                            tx.RollBack();
                            return CommandResult.Fail("NO_PLAN_VIEW: level '" + level.Name + "' has no floor plan and the project has no floor plan view type; pass view_id.");
                        }
                        plan = ViewPlan.Create(doc, planType.Id, level.Id);
                    }
                    var sketchPlane = SketchPlane.Create(doc, level.Id);
                    var created = doc.Create.NewRoomBoundaryLines(sketchPlane, curves, plan);
                    var ids = new List<long>();
                    var lines = new List<ModelCurve>();
                    foreach (ModelCurve curve in created) { ids.Add(RevitCompat.GetId(curve.Id)); lines.Add(curve); }
                    JObject viewInfo = new JObject { ["element_id"] = RevitCompat.GetId(plan.Id), ["name"] = plan.Name, ["view_type"] = plan.ViewType.ToString() };
                    if (temporaryView)
                    {
                        doc.Delete(plan.Id);
                        if (lines.Any(l => !l.IsValidObject))
                        {
                            tx.RollBack();
                            return CommandResult.Fail("CREATE_FAILED: the separation lines went with the temporary floor plan of level '" + level.Name + "'; give the level a floor plan (view_id).");
                        }
                        viewInfo = new JObject { ["temporary"] = true, ["note"] = "drawn through a temporary floor plan of the level, deleted again" };
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                    return CommandResult.Ok(new JObject
                    {
                        ["view"] = viewInfo,
                        ["level"] = RoomsHelpers.Ref(level),
                        ["curve_count"] = ids.Count,
                        ["separator_ids"] = new JArray(ids),
                        ["length_mm"] = Mm.FromFeet(lengthFeet),
                        ["closed_loop"] = closeLoop
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the room separation lines: " + ex.Message);
                }
            }
        }

        private static bool TryPoints(JArray points, double z, double shortCurve, bool closed, out List<XYZ> result, out CommandResult failure)
        {
            result = new List<XYZ>();
            failure = null;
            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i] as JObject;
                var x = point?["x"];
                var y = point?["y"];
                var extra = point?.Properties().Select(pr => pr.Name).FirstOrDefault(n => n != "x" && n != "y");
                if (x == null || y == null || (x.Type != JTokenType.Integer && x.Type != JTokenType.Float)
                    || (y.Type != JTokenType.Integer && y.Type != JTokenType.Float) || extra != null)
                {
                    failure = CommandResult.Fail("INVALID_POINTS: point " + i + " must be exactly {\"x\": mm, \"y\": mm}"
                        + (extra != null ? " (unknown field '" + extra + "')" : "") + ".");
                    return false;
                }
                result.Add(new XYZ(Mm.ToFeet(x.Value<double>()), Mm.ToFeet(y.Value<double>()), z));
            }
            var pairs = closed ? result.Count : result.Count - 1;
            for (var i = 0; i < pairs; i++)
            {
                var j = (i + 1) % result.Count;
                if (result[i].DistanceTo(result[j]) <= shortCurve)
                {
                    failure = CommandResult.Fail("INVALID_POINTS: points " + i + " and " + j
                        + " are too close; remove repeated points" + (closed ? " (do not repeat the first point at the end)." : "."));
                    return false;
                }
            }
            return true;
        }
    }
}
