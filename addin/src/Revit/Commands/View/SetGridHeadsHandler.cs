// New in Plinth (Step 9 drawing set): grid heads outside the building in one drawing view.
// Grids are modelled as long as the source drew them, so a short grid (one that stops at an interior wall) shows its
// bubble inside the plan, and a section shows the bubbles wherever the grids' model extents end. Here each grid's
// view-specific (2D) extent in the given view is set: in a plan it runs across the building box and head_offset_mm
// (paper mm, times the view scale) beyond it at both ends, with a bubble at both ends; in a section or elevation it
// runs from the box bottom to head_offset_mm above its top, with the bubble at the top only. Only this view changes:
// the grids' model (3D) extents, other views and the grid types stay as they are, and deleting the view drops it all.
// The caller makes the crop region large enough for the heads (Revit stops datums at the crop region). Grids the view
// does not show (parallel to a section, or off the building box) are listed as skipped, not failures: a call fails
// only when grids the view shows could not be set and none was.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Plinth.Revit.Core.Drawing;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.View
{
    // Plinth.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class SetGridHeadsHandler : IRevitCommand
    {
        private const double DefaultOffsetMm = 10.0;

        public string Name => "set_grid_heads";
        public string Description =>
            "Put grid heads outside the building in one view (view_id), for the grids element_ids only, through their view-specific "
            + "(2D) extents. bounds is the building box in model mm: plans use min_x_mm/min_y_mm/max_x_mm/max_y_mm and get each grid "
            + "running head_offset_mm (paper mm, default 10, times the view scale) past the box at both ends with bubbles at both "
            + "ends; sections and elevations use min_z_mm/max_z_mm and get each grid from the box bottom to head_offset_mm above "
            + "its top, bubble at the top. The model extents of the grids are not changed. Make the crop region hold the heads first.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""view_id"":{""type"":""integer""},
            ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""bounds"":{""type"":""object""},
            ""head_offset_mm"":{""type"":""number""}},
            ""required"":[""view_id"",""element_ids"",""bounds""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!Lookup.TryView(doc, p.Value<long>("view_id"), out View view, out var failure)) return failure;
            var plan = view is ViewPlan;
            if (!plan && !(view is ViewSection))
                return CommandResult.Fail("VIEW_NOT_SUPPORTED: '" + view.Name + "' is a " + view.ViewType
                    + " view; grid heads are set in plans, sections and elevations.");

            var bounds = p["bounds"] as JObject;
            var keys = plan ? new[] { "min_x_mm", "min_y_mm", "max_x_mm", "max_y_mm" } : new[] { "min_z_mm", "max_z_mm" };
            var missing = keys.Where(k => bounds?[k] == null || bounds[k].Type == JTokenType.Null).ToList();
            if (missing.Count > 0)
                return CommandResult.Fail("INVALID_PARAMS: bounds needs " + string.Join(", ", missing) + " for a " + view.ViewType + " view.");
            double B(string k) => Mm.ToFeet(bounds.Value<double>(k));
            var offsetMm = p.Value<double?>("head_offset_mm") ?? DefaultOffsetMm;
            if (offsetMm < 0) return CommandResult.Fail("INVALID_PARAMS: head_offset_mm cannot be negative.");
            var offset = Mm.ToFeet(offsetMm * Math.Max(1, view.Scale));

            var ids = p["element_ids"]?.ToObject<long[]>() ?? new long[0];
            if (ids.Length == 0) return CommandResult.Fail("INVALID_PARAMS: element_ids cannot be empty.");

            var done = new JArray();
            var skipped = new JArray();
            var failed = 0;  // grids the view shows that could not be set (unseen ones are not failures)
            void Skip(long id, string reason, bool seen = true)
            {
                skipped.Add(new JObject { ["element_id"] = id, ["reason"] = reason });
                if (seen) failed++;
            }

            using (var tx = new Transaction(doc, "Plinth: Set grid heads"))
            {
                tx.Start();
                foreach (var id in ids.Distinct())
                {
                    if (!(doc.GetElement(RevitCompat.ToElementId(id)) is Grid grid))
                    {
                        Skip(id, "not a grid");
                        continue;
                    }
                    if (!grid.CanBeVisibleInView(view))
                    {
                        Skip(id, "not seen in this view", false);
                        continue;
                    }
                    var line = (grid.GetCurvesInView(DatumExtentType.ViewSpecific, view).FirstOrDefault()
                        ?? grid.GetCurvesInView(DatumExtentType.Model, view).FirstOrDefault()) as Line;
                    if (line == null)
                    {
                        Skip(id, "not a straight grid in this view");
                        continue;
                    }
                    XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
                    XYZ start, end;
                    DatumEnds? bubbleOnly = null;
                    if (plan)
                    {
                        var s = GridHeads.ClipToBox(a.X, a.Y, b.X, b.Y, B("min_x_mm") - offset, B("min_y_mm") - offset,
                            B("max_x_mm") + offset, B("max_y_mm") + offset);
                        if (s == null)
                        {
                            Skip(id, "does not cross the building box", false);
                            continue;
                        }
                        start = new XYZ(s.X0, s.Y0, a.Z);
                        end = new XYZ(s.X1, s.Y1, b.Z);
                    }
                    else
                    {
                        if (Math.Abs(a.X - b.X) > 1e-6 || Math.Abs(a.Y - b.Y) > 1e-6)
                        {
                            Skip(id, "not vertical in this view");
                            continue;
                        }
                        var upward = a.Z <= b.Z;
                        double bottom = B("min_z_mm"), top = B("max_z_mm") + offset;
                        start = new XYZ(a.X, a.Y, upward ? bottom : top);
                        end = new XYZ(b.X, b.Y, upward ? top : bottom);
                        bubbleOnly = upward ? DatumEnds.End1 : DatumEnds.End0;
                    }
                    if (start.DistanceTo(end) <= app.Application.ShortCurveTolerance)
                    {
                        Skip(id, "the box leaves no length");
                        continue;
                    }
                    var curve = Line.CreateBound(start, end);
                    grid.SetDatumExtentType(DatumEnds.End0, view, DatumExtentType.ViewSpecific);
                    grid.SetDatumExtentType(DatumEnds.End1, view, DatumExtentType.ViewSpecific);
                    if (!grid.IsCurveValidInView(DatumExtentType.ViewSpecific, view, curve))
                    {
                        Skip(id, "Revit does not accept that extent in this view");
                        continue;
                    }
                    grid.SetCurveInView(DatumExtentType.ViewSpecific, view, curve);
                    foreach (var at in new[] { DatumEnds.End0, DatumEnds.End1 })
                    {
                        if (bubbleOnly == null || bubbleOnly == at) grid.ShowBubbleInView(at, view);
                        else grid.HideBubbleInView(at, view);
                    }
                    done.Add(new JObject
                    {
                        ["element_id"] = id,
                        ["name"] = grid.Name,
                        ["start_mm"] = PointMm(start),
                        ["end_mm"] = PointMm(end),
                        ["bubbles"] = bubbleOnly == null ? "both" : "top"
                    });
                }
                if (done.Count == 0 && failed > 0)
                {
                    tx.RollBack();
                    return CommandResult.Fail("NOTHING_CHANGED: no grid could be set in '" + view.Name + "'.", new JObject { ["skipped"] = skipped });
                }
                if (done.Count == 0) tx.RollBack();  // none of the grids shows here: nothing to do
                else if (tx.Commit() != TransactionStatus.Committed)
                    return CommandResult.Fail("NOT_COMMITTED: Revit rolled the change back; see failures. Nothing changed.");
            }
            return CommandResult.Ok(new JObject
            {
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["head_offset_mm"] = offsetMm,
                ["grids"] = done,
                ["skipped"] = skipped
            });
        }

        private static JObject PointMm(XYZ point) => new JObject
        {
            ["x"] = Mm.FromFeet(point.X),
            ["y"] = Mm.FromFeet(point.Y),
            ["z"] = Mm.FromFeet(point.Z)
        };
    }
}
