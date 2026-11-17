// New in Plinth: places a plan's dimension chains the way an architect does, so the drawing set reproduces the source
// sheets (PRD Step 9, stage 9 "Drawings"). Every chain is a live dimension: its references are wall faces (from the
// wall solids, computed with references, crossing the view's cut plane), grids, and door/window Left/Right
// reference planes, so the dimensions follow the model when it changes.
//
// Modes and where their chains go, nearest to the building first on each side:
//   exterior_openings  the exterior wall faces seen from that side (a skyline of the faces whose normal points out),
//                      stopping at each outline corner and at both edges of every door/window in those walls;
//   grids              grid to grid, for the grids crossing that side;
//   overall            the building's outermost wall faces on that axis;
//   interior_rooms     inside each room, its clear width and length, from the room point to the nearest wall face
//                      on each side (not tied to a side).
// The first chain is offset_mm beyond the building on its side, each further one spacing_mm beyond the last.
// Stops closer than merge_tolerance_mm merge (a wall corner wins over a grid, a grid over an opening edge), so no
// chain has a zero-length segment; a chain that would repeat another one on the same side, or lie on top of one, is
// skipped. Every skipped chain and stop is reported with its reason. Each chain is its own transaction inside one
// undo group: a Revit warning rolls back that chain only (reported in skipped) unless all_or_nothing is set.
// Limits: walls and grids must run along the model X/Y axes (others are reported, not dimensioned); curtain walls and
// columns are not used; opening edges assume the family's Left/Right planes sit at -/+ width/2 (Revit's standard
// door and window templates).

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Plinth.Revit.Core.Drawing;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Annotation
{
    // Plinth.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class AutoDimensionPlanHandler : IRevitCommand
    {
        private static readonly string[] ModeNames = { "exterior_openings", "grids", "overall", "interior_rooms" };
        private static readonly string[] DefaultModes = { "exterior_openings", "grids", "overall" };
        private const double MinPieceMm = 10.0;

        public string Name => "auto_dimension_plan";
        public string Description =>
            "New in Plinth: dimension a plan view (view_id, default the active view) like an architect. modes: any of exterior_openings "
            + "(each exterior wall face: corners and door/window edges), grids (grid to grid), overall (outer wall faces), "
            + "interior_rooms (each room's clear width and length; room_ids to limit). Default exterior_openings, grids, overall. "
            + "sides: any of top (+Y), bottom, left, right (default all). offset_mm (default 800) from the building to the first "
            + "chain, spacing_mm (default 600) between chains; merge_tolerance_mm (default 1) merges coinciding stops. "
            + "Returns each dimension id with its stops and Revit's segment values, and every skipped chain or stop with the reason.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""view_id"":{""type"":""integer""},
            ""modes"":{""type"":""array"",""items"":{""type"":""string""}},
            ""sides"":{""type"":""array"",""items"":{""type"":""string""}},
            ""offset_mm"":{""type"":""number""},
            ""spacing_mm"":{""type"":""number""},
            ""merge_tolerance_mm"":{""type"":""number""},
            ""room_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""dimension_type_id"":{""type"":""integer""},
            ""all_or_nothing"":{""type"":""boolean""}}}";
        public bool ReadOnly => false;

        private sealed class PlanFace
        {
            public int Axis;        // normal axis: 0 = X, 1 = Y
            public int Sign;        // normal direction along that axis
            public double Level;    // mm along the normal axis
            public double Min, Max; // mm along the other axis
            public long WallId;
            public int RefKey;
        }

        private sealed class PlannedChain
        {
            public string Mode;
            public PlanSide? Side;
            public long? RoomId;
            public int MeasureAxis;
            public double LinePos;
            public ChainBuildResult Chain;
        }

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!AnnotationHelpers.TryAnnotationView(doc, p.Value<long?>("view_id"), out View view, out var failure)) return failure;
            if (!(view is ViewPlan plan))
                return CommandResult.Fail("NOT_A_PLAN_VIEW: auto_dimension_plan needs a plan view; '" + view.Name + "' is a " + view.ViewType + ".");

            if (!DrawingSupport.TryStringSet(p, "modes", ModeNames, DefaultModes, out var modes, out failure)) return failure;
            if (!DrawingSupport.TryStringSet(p, "sides", SideMath.PlanSideNames, SideMath.PlanSideNames, out var sideNames, out failure)) return failure;
            var sides = sideNames.Select(n => { SideMath.TryParse(n, out var s); return s; }).ToList();
            if (!DrawingSupport.TryNonNegative(p, "offset_mm", 800, out var offset, out failure)) return failure;
            if (!DrawingSupport.TryNonNegative(p, "spacing_mm", 600, out var spacing, out failure)) return failure;
            if (!DrawingSupport.TryNonNegative(p, "merge_tolerance_mm", 1, out var tol, out failure)) return failure;
            if (tol < 0.1) return CommandResult.Fail("INVALID_PARAMS: merge_tolerance_mm must be at least 0.1.");
            if (modes.Contains("grids") || modes.Contains("overall") || modes.Contains("exterior_openings"))
                if (spacing < 1) return CommandResult.Fail("INVALID_PARAMS: spacing_mm must be at least 1 so chains do not overlap.");
            if (!DrawingSupport.TryDimensionType(doc, p.Value<long?>("dimension_type_id"), out var dimType, out failure)) return failure;
            var allOrNothing = p.Value<bool?>("all_or_nothing") ?? false;
            if (p["room_ids"] != null && !modes.Contains("interior_rooms"))
                return CommandResult.Fail("INVALID_PARAMS: room_ids applies only to mode interior_rooms; add it to modes or drop room_ids.");

            var skipped = new JArray();
            var cutZ = CutHeight(doc, plan);
            var viewZ = plan.GenLevel?.ProjectElevation ?? 0;

            // Index the wall faces crossing the cut plane.
            var refs = new List<Reference>();
            var faces = IndexWallFaces(doc, plan, cutZ, refs, skipped);
            if (faces.Count == 0 && !modes.SequenceEqual(new[] { "grids" }))
                skipped.Add(Skip("all", null, null, "no wall in this view crosses the cut plane at " + SideMath.Mm(DrawingSupport.ToMm(cutZ)) + " mm"));

            double[] box = null; // xMin, yMin, xMax, yMax in mm
            foreach (var f in faces)
            {
                double fx0 = f.Axis == 0 ? f.Level : f.Min, fx1 = f.Axis == 0 ? f.Level : f.Max;
                double fy0 = f.Axis == 1 ? f.Level : f.Min, fy1 = f.Axis == 1 ? f.Level : f.Max;
                box = box == null ? new[] { fx0, fy0, fx1, fy1 }
                    : new[] { Math.Min(box[0], fx0), Math.Min(box[1], fy0), Math.Max(box[2], fx1), Math.Max(box[3], fy1) };
            }

            var grids = modes.Contains("grids") ? IndexGrids(doc, plan, refs, skipped) : new List<Tuple<int, double, int, string>>();
            var openings = modes.Contains("exterior_openings") ? IndexOpenings(doc, plan, refs, skipped) : new List<Opening>();

            // Plan the chains.
            var planned = new List<PlannedChain>();
            if (box == null && grids.Count > 0)
            {
                var xs = grids.Where(g => g.Item1 == 0).Select(g => g.Item2).DefaultIfEmpty(0).ToList();
                var ys = grids.Where(g => g.Item1 == 1).Select(g => g.Item2).DefaultIfEmpty(0).ToList();
                box = new[] { xs.Min(), ys.Min(), xs.Max(), ys.Max() };
            }
            if (box != null)
            {
                foreach (var side in sides)
                {
                    var mAxis = SideMath.MeasureAxis(side);
                    var sign = SideMath.OutwardSign(side);
                    var outer = SideMath.OuterExtent(mAxis == 0 ? box[1] : box[0], mAxis == 0 ? box[3] : box[2], sign);
                    var onSide = new List<PlannedChain>();
                    foreach (var mode in DefaultModes) // nearest first: exterior_openings, grids, overall
                    {
                        if (!modes.Contains(mode)) continue;
                        ChainBuildResult chain;
                        switch (mode)
                        {
                            case "exterior_openings": chain = ExteriorChain(side, faces, openings, tol, skipped); break;
                            case "grids":
                                chain = ChainBuilder.Build(grids.Where(g => g.Item1 == mAxis)
                                    .Select(g => new ChainStop(g.Item2, 1, g.Item4, g.Item3)), tol);
                                break;
                            default: chain = OverallChain(mAxis, faces, tol); break;
                        }
                        if (chain.SkipReason != null)
                        {
                            skipped.Add(Skip(mode, side, null, chain.SkipReason));
                            continue;
                        }
                        var same = onSide.FirstOrDefault(c => ChainBuilder.SameChain(c.Chain.Positions, chain.Positions, tol));
                        if (same != null)
                        {
                            skipped.Add(Skip(mode, side, null, "same stops as the " + same.Mode + " chain on this side"));
                            continue;
                        }
                        var pc = new PlannedChain
                        {
                            Mode = mode, Side = side, MeasureAxis = mAxis, Chain = chain,
                            LinePos = SideMath.ChainLine(outer, sign, offset, spacing, onSide.Count)
                        };
                        onSide.Add(pc);
                        planned.Add(pc);
                    }
                }
            }
            if (modes.Contains("interior_rooms"))
            {
                if (!TryRooms(doc, plan, p, out var rooms, out failure)) return failure;
                foreach (var room in rooms) planned.AddRange(RoomChains(room, faces, tol, skipped));
            }

            if (planned.Count == 0)
                return CommandResult.Fail("NOTHING_TO_DIMENSION: no chain could be planned in view '" + view.Name + "'; see skipped.",
                    new JObject { ["skipped"] = skipped });

            // Create them: one transaction per chain inside one undo group.
            var created = new JArray();
            var lines = new List<double[]>(); // axis, pos, min, max
            var failedCount = 0;
            using (var group = new TransactionGroup(doc, "Plinth: Auto Dimension Plan"))
            {
                group.Start();
                try
                {
                    foreach (var pc in planned)
                    {
                        var min = pc.Chain.Stops[0].Position;
                        var max = pc.Chain.Stops[pc.Chain.Stops.Count - 1].Position;
                        var clash = lines.FirstOrDefault(l => (int)l[0] == pc.MeasureAxis && Math.Abs(l[1] - pc.LinePos) <= tol
                            && min < l[3] - tol && max > l[2] + tol);
                        if (clash != null)
                        {
                            skipped.Add(Skip(pc.Mode, pc.Side, pc.RoomId, "would lie on top of another chain on the same line"));
                            continue;
                        }
                        var line = pc.MeasureAxis == 0
                            ? Line.CreateBound(new XYZ(DrawingSupport.ToFeet(min), DrawingSupport.ToFeet(pc.LinePos), viewZ),
                                new XYZ(DrawingSupport.ToFeet(max), DrawingSupport.ToFeet(pc.LinePos), viewZ))
                            : Line.CreateBound(new XYZ(DrawingSupport.ToFeet(pc.LinePos), DrawingSupport.ToFeet(min), viewZ),
                                new XYZ(DrawingSupport.ToFeet(pc.LinePos), DrawingSupport.ToFeet(max), viewZ));
                        var array = new ReferenceArray();
                        foreach (var s in pc.Chain.Stops) array.Append(refs[s.Key]);
                        Dimension dim = null;
                        var reason = DrawingSupport.RunItem(doc, "Plinth: Dimension chain", () =>
                        {
                            dim = dimType != null ? doc.Create.NewDimension(plan, line, array, dimType) : doc.Create.NewDimension(plan, line, array);
                            if (dim == null) throw new InvalidOperationException("Revit returned no dimension.");
                        });
                        if (reason != null)
                        {
                            failedCount++;
                            skipped.Add(Skip(pc.Mode, pc.Side, pc.RoomId, "Revit refused the chain: " + reason));
                            if (allOrNothing) break;
                            continue;
                        }
                        lines.Add(new[] { pc.MeasureAxis, pc.LinePos, min, max });
                        created.Add(new JObject
                        {
                            ["dimension_id"] = RevitCompat.GetId(dim.Id),
                            ["mode"] = pc.Mode,
                            ["side"] = pc.Side.HasValue ? SideMath.Name(pc.Side.Value) : null,
                            ["room_id"] = pc.RoomId,
                            ["measures"] = pc.MeasureAxis == 0 ? "x" : "y",
                            ["line_at_mm"] = Math.Round(pc.LinePos, 1),
                            ["stops"] = DrawingSupport.Stops(pc.Chain.Stops),
                            ["planned_segments_mm"] = DrawingSupport.Segments(pc.Chain),
                            ["values_mm"] = DrawingSupport.Values(dim),
                            ["merged_stops"] = DrawingSupport.Merged(pc.Chain.Merged)
                        });
                    }
                    if (allOrNothing && failedCount > 0)
                    {
                        group.RollBack();
                        return CommandResult.Fail("CREATE_FAILED: " + failedCount + " chain(s) failed and all_or_nothing is set; nothing was created.",
                            new JObject { ["skipped"] = skipped });
                    }
                    DrawingSupport.Finish(group, created.Count > 0);
                }
                catch (Exception ex)
                {
                    if (group.HasStarted() && !group.HasEnded()) group.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not dimension the plan: " + ex.Message + " Nothing was created.");
                }
            }

            if (created.Count == 0)
                return CommandResult.Fail("CREATE_FAILED: Revit created none of the " + planned.Count + " planned chain(s); see skipped. Nothing was created.",
                    new JObject { ["skipped"] = skipped });
            return CommandResult.Ok(new JObject
            {
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["view_name"] = view.Name,
                ["cut_height_mm"] = Math.Round(DrawingSupport.ToMm(cutZ), 1),
                ["wall_faces_indexed"] = faces.Count,
                ["created_count"] = created.Count,
                ["dimension_ids"] = new JArray(created.Select(d => d["dimension_id"])),
                ["dimensions"] = created,
                ["skipped"] = skipped
            });
        }

        private static JObject Skip(string mode, PlanSide? side, long? roomId, string reason) => new JObject
        {
            ["mode"] = mode,
            ["side"] = side.HasValue ? SideMath.Name(side.Value) : null,
            ["room_id"] = roomId,
            ["reason"] = reason
        };

        private static double CutHeight(Document doc, ViewPlan plan)
        {
            var range = plan.GetViewRange();
            var level = doc.GetElement(range.GetLevelId(PlanViewPlane.CutPlane)) as Level ?? plan.GenLevel;
            return (level?.ProjectElevation ?? 0) + range.GetOffset(PlanViewPlane.CutPlane);
        }

        /// <summary>Vertical planar faces of the walls in the view that cross the cut plane and face along X or Y.</summary>
        private static List<PlanFace> IndexWallFaces(Document doc, ViewPlan plan, double cutZ, List<Reference> refs, JArray skipped)
        {
            var result = new List<PlanFace>();
            var options = new Options { ComputeReferences = true, View = plan };
            var zTol = DrawingSupport.ToFeet(1);
            foreach (var wall in new FilteredElementCollector(doc, plan.Id).OfClass(typeof(Wall)).Cast<Wall>())
            {
                if (wall.WallType.Kind == WallKind.Curtain) continue;
                var offAxis = false;
                var count = 0;
                GeometryElement geometry;
                try { geometry = wall.get_Geometry(options); }
                catch (Exception) { geometry = null; }
                if (geometry == null) continue;
                foreach (var solid in geometry.OfType<Solid>().Where(s => s.Volume > 0))
                {
                    foreach (Face face in solid.Faces)
                    {
                        if (!(face is PlanarFace pf) || pf.Reference == null) continue;
                        var n = pf.FaceNormal;
                        if (Math.Abs(n.Z) > 1e-6) continue;
                        int axis;
                        if (Math.Abs(n.X) > 0.9999) axis = 0;
                        else if (Math.Abs(n.Y) > 0.9999) axis = 1;
                        else { offAxis = true; continue; }
                        double zMin = double.MaxValue, zMax = double.MinValue, sMin = double.MaxValue, sMax = double.MinValue;
                        foreach (EdgeArray loop in pf.EdgeLoops)
                            foreach (Edge edge in loop)
                                foreach (var pt in edge.Tessellate())
                                {
                                    zMin = Math.Min(zMin, pt.Z);
                                    zMax = Math.Max(zMax, pt.Z);
                                    var s = axis == 0 ? pt.Y : pt.X;
                                    sMin = Math.Min(sMin, s);
                                    sMax = Math.Max(sMax, s);
                                }
                        if (zMin > cutZ + zTol || zMax < cutZ - zTol || sMax <= sMin) continue;
                        refs.Add(pf.Reference);
                        result.Add(new PlanFace
                        {
                            Axis = axis,
                            Sign = (axis == 0 ? n.X : n.Y) > 0 ? 1 : -1,
                            Level = DrawingSupport.ToMm(axis == 0 ? pf.Origin.X : pf.Origin.Y),
                            Min = DrawingSupport.ToMm(sMin),
                            Max = DrawingSupport.ToMm(sMax),
                            WallId = RevitCompat.GetId(wall.Id),
                            RefKey = refs.Count - 1
                        });
                        count++;
                    }
                }
                if (offAxis && count == 0)
                    skipped.Add(new JObject
                    {
                        ["mode"] = "all",
                        ["element_id"] = RevitCompat.GetId(wall.Id),
                        ["reason"] = "wall does not run along the model X/Y axes; not dimensioned"
                    });
            }
            return result;
        }

        /// <summary>(measure axis, position mm, ref key, source) for each straight grid along X or Y.</summary>
        private static List<Tuple<int, double, int, string>> IndexGrids(Document doc, ViewPlan plan, List<Reference> refs, JArray skipped)
        {
            var result = new List<Tuple<int, double, int, string>>();
            foreach (var grid in new FilteredElementCollector(doc, plan.Id).OfClass(typeof(Grid)).Cast<Grid>())
            {
                if (!(grid.Curve is Line line))
                {
                    skipped.Add(new JObject { ["mode"] = "grids", ["element_id"] = RevitCompat.GetId(grid.Id), ["reason"] = "grid " + grid.Name + " is not straight" });
                    continue;
                }
                var d = line.Direction;
                int measure;
                if (Math.Abs(d.Y) > 0.9999) measure = 0;      // runs along Y: measured along X
                else if (Math.Abs(d.X) > 0.9999) measure = 1; // runs along X: measured along Y
                else
                {
                    skipped.Add(new JObject { ["mode"] = "grids", ["element_id"] = RevitCompat.GetId(grid.Id), ["reason"] = "grid " + grid.Name + " is not along the model X/Y axes" });
                    continue;
                }
                refs.Add(new Reference(grid));
                var o = line.GetEndPoint(0);
                result.Add(Tuple.Create(measure, DrawingSupport.ToMm(measure == 0 ? o.X : o.Y), refs.Count - 1, "grid " + grid.Name));
            }
            return result;
        }

        private sealed class Opening
        {
            public long HostId;
            public double Center; // mm along the host wall's axis
            public int Axis;      // host wall runs along: 0 = X, 1 = Y
            public List<ChainStop> Stops = new List<ChainStop>();
        }

        /// <summary>Doors and windows hosted by straight X/Y walls, with their Left/Right (else centre) references.</summary>
        private static List<Opening> IndexOpenings(Document doc, ViewPlan plan, List<Reference> refs, JArray skipped)
        {
            var result = new List<Opening>();
            var instances = new FilteredElementCollector(doc, plan.Id).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(fi => fi.Category != null && (fi.Category.BuiltInCategory == BuiltInCategory.OST_Doors
                    || fi.Category.BuiltInCategory == BuiltInCategory.OST_Windows) && fi.Host is Wall);
            foreach (var fi in instances)
            {
                var id = RevitCompat.GetId(fi.Id);
                var what = (fi.Category.BuiltInCategory == BuiltInCategory.OST_Doors ? "door " : "window ") + id;
                var hostCurve = ((fi.Host as Wall).Location as LocationCurve)?.Curve as Line;
                var location = (fi.Location as LocationPoint)?.Point;
                if (hostCurve == null || location == null) continue;
                int axis;
                if (Math.Abs(hostCurve.Direction.X) > 0.9999) axis = 0;
                else if (Math.Abs(hostCurve.Direction.Y) > 0.9999) axis = 1;
                else continue;
                var opening = new Opening
                {
                    HostId = RevitCompat.GetId(fi.Host.Id),
                    Axis = axis,
                    Center = DrawingSupport.ToMm(axis == 0 ? location.X : location.Y)
                };
                var left = fi.GetReferences(FamilyInstanceReferenceType.Left).FirstOrDefault();
                var right = fi.GetReferences(FamilyInstanceReferenceType.Right).FirstOrDefault();
                var width = Width(fi);
                if (left != null && right != null && width.HasValue)
                {
                    var t = fi.GetTransform();
                    var l = t.OfPoint(new XYZ(-width.Value / 2, 0, 0));
                    var r = t.OfPoint(new XYZ(width.Value / 2, 0, 0));
                    refs.Add(left);
                    opening.Stops.Add(new ChainStop(DrawingSupport.ToMm(axis == 0 ? l.X : l.Y), 2, what + " left", refs.Count - 1));
                    refs.Add(right);
                    opening.Stops.Add(new ChainStop(DrawingSupport.ToMm(axis == 0 ? r.X : r.Y), 2, what + " right", refs.Count - 1));
                }
                else
                {
                    var centre = fi.GetReferences(FamilyInstanceReferenceType.CenterLeftRight).FirstOrDefault();
                    if (centre == null)
                    {
                        skipped.Add(new JObject { ["mode"] = "exterior_openings", ["element_id"] = id, ["reason"] = what + " has no Left/Right or centre reference planes" });
                        continue;
                    }
                    refs.Add(centre);
                    opening.Stops.Add(new ChainStop(opening.Center, 2, what + " centre", refs.Count - 1));
                    skipped.Add(new JObject { ["mode"] = "exterior_openings", ["element_id"] = id,
                        ["reason"] = what + ": edges not dimensioned (no Left/Right reference or width); its centre is used" });
                }
                result.Add(opening);
            }
            return result;
        }

        private static double? Width(FamilyInstance fi)
        {
            var bips = fi.Category.BuiltInCategory == BuiltInCategory.OST_Doors
                ? new[] { BuiltInParameter.DOOR_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM }
                : new[] { BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM };
            foreach (var bip in bips)
            {
                var param = fi.get_Parameter(bip) ?? fi.Symbol?.get_Parameter(bip);
                if (param != null && param.HasValue && param.StorageType == StorageType.Double && param.AsDouble() > 0) return param.AsDouble();
            }
            var named = fi.LookupParameter("Width") ?? fi.Symbol?.LookupParameter("Width");
            return named != null && named.HasValue && named.StorageType == StorageType.Double && named.AsDouble() > 0 ? named.AsDouble() : (double?)null;
        }

        /// <summary>Corners of the exterior faces seen from the side, plus the edges of the openings in those faces' walls.</summary>
        private static ChainBuildResult ExteriorChain(PlanSide side, List<PlanFace> faces, List<Opening> openings, double tol, JArray skipped)
        {
            var nAxis = SideMath.NormalAxis(side);
            var mAxis = SideMath.MeasureAxis(side);
            var sign = SideMath.OutwardSign(side);
            var outward = faces.Where(f => f.Axis == nAxis && f.Sign == sign).ToList();
            var pieces = Skyline.Visible(outward.Select((f, i) => new FaceSpan(f.Level, f.Min, f.Max, i)), sign, MinPieceMm);
            var stops = new List<ChainStop>();
            var usedOpenings = new HashSet<Opening>();
            foreach (var piece in pieces)
            {
                var face = outward[piece.Face.Key];
                foreach (var end in new[] { piece.Min, piece.Max })
                {
                    var corner = faces.Where(f => f.Axis == mAxis && Math.Abs(f.Level - end) <= tol
                            && face.Level >= f.Min - tol && face.Level <= f.Max + tol)
                        .OrderBy(f => Math.Abs(f.Level - end)).FirstOrDefault();
                    if (corner == null)
                        skipped.Add(new JObject
                        {
                            ["mode"] = "exterior_openings",
                            ["side"] = SideMath.Name(side),
                            ["reason"] = "no wall face at the outline point " + SideMath.Mm(end) + " mm of wall " + face.WallId + "; stop left out"
                        });
                    else
                        stops.Add(new ChainStop(corner.Level, 0, "wall " + corner.WallId + " face", corner.RefKey));
                }
                foreach (var opening in openings)
                {
                    if (opening.HostId != face.WallId || opening.Axis != mAxis || usedOpenings.Contains(opening)) continue;
                    if (opening.Center < piece.Min - tol || opening.Center > piece.Max + tol) continue;
                    usedOpenings.Add(opening);
                    stops.AddRange(opening.Stops);
                }
            }
            return ChainBuilder.Build(stops, tol);
        }

        /// <summary>The outermost faces along the measure axis: the lowest face pointing -axis and the highest pointing +axis.</summary>
        private static ChainBuildResult OverallChain(int mAxis, List<PlanFace> faces, double tol)
        {
            var low = faces.Where(f => f.Axis == mAxis && f.Sign < 0).OrderBy(f => f.Level).FirstOrDefault();
            var high = faces.Where(f => f.Axis == mAxis && f.Sign > 0).OrderByDescending(f => f.Level).FirstOrDefault();
            var stops = new List<ChainStop>();
            if (low != null) stops.Add(new ChainStop(low.Level, 0, "wall " + low.WallId + " face", low.RefKey));
            if (high != null) stops.Add(new ChainStop(high.Level, 0, "wall " + high.WallId + " face", high.RefKey));
            return ChainBuilder.Build(stops, tol);
        }

        private static bool TryRooms(Document doc, ViewPlan plan, JObject p, out List<Room> rooms, out CommandResult failure)
        {
            failure = null;
            var inView = new FilteredElementCollector(doc, plan.Id).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<Room>().Where(r => r.Area > 0 && r.Location is LocationPoint).ToList();
            rooms = inView;
            if (!(p["room_ids"] is JArray ids)) return true;
            var wanted = ids.Select(t => t.Value<long>()).Distinct().ToList();
            var missing = wanted.Where(id => inView.All(r => RevitCompat.GetId(r.Id) != id)).ToList();
            if (missing.Count > 0)
            {
                failure = CommandResult.Fail("ROOM_NOT_FOUND: room id(s) " + string.Join(", ", missing) + " are not placed rooms in view '" + plan.Name + "'.",
                    new JObject { ["valid_room_ids"] = new JArray(inView.Take(200).Select(r => RevitCompat.GetId(r.Id))) });
                return false;
            }
            rooms = inView.Where(r => wanted.Contains(RevitCompat.GetId(r.Id))).ToList();
            return true;
        }

        /// <summary>Width (along X) and length (along Y) of a room: the nearest wall face facing it on each side of its point.</summary>
        private static IEnumerable<PlannedChain> RoomChains(Room room, List<PlanFace> faces, double tol, JArray skipped)
        {
            var roomId = RevitCompat.GetId(room.Id);
            var pt = ((LocationPoint)room.Location).Point;
            var px = DrawingSupport.ToMm(pt.X);
            var py = DrawingSupport.ToMm(pt.Y);
            FaceSpan Span(PlanFace f) => new FaceSpan(f.Level, f.Min, f.Max, f.RefKey);
            PlanFace Find(int axis, int faceSign, double origin, double across, int dir)
            {
                var candidates = faces.Where(f => f.Axis == axis && f.Sign == faceSign).ToList();
                var hit = Raycast.Nearest(candidates.Select(Span), origin, across, dir, tol);
                return hit == null ? null : candidates.First(f => f.RefKey == hit.Key);
            }
            var left = Find(0, 1, px, py, -1);
            var right = Find(0, -1, px, py, 1);
            var down = Find(1, 1, py, px, -1);
            var up = Find(1, -1, py, px, 1);
            var result = new List<PlannedChain>();
            foreach (var axis in new[] { 0, 1 })
            {
                var a = axis == 0 ? left : down;
                var b = axis == 0 ? right : up;
                if (a == null || b == null)
                {
                    skipped.Add(Skip("interior_rooms", null, roomId, "no wall face found on both sides of the room point along " + (axis == 0 ? "X" : "Y")));
                    continue;
                }
                var chain = ChainBuilder.Build(new[]
                {
                    new ChainStop(a.Level, 0, "wall " + a.WallId + " face", a.RefKey),
                    new ChainStop(b.Level, 0, "wall " + b.WallId + " face", b.RefKey)
                }, tol);
                if (chain.SkipReason != null)
                {
                    skipped.Add(Skip("interior_rooms", null, roomId, chain.SkipReason));
                    continue;
                }
                // The width line sits halfway between the room point and the lower wall, the length line halfway to the left wall,
                // so neither crosses the room tag at the room point.
                var lineSide = axis == 0 ? down : left;
                var origin = axis == 0 ? py : px;
                result.Add(new PlannedChain
                {
                    Mode = "interior_rooms",
                    RoomId = roomId,
                    MeasureAxis = axis,
                    Chain = chain,
                    LinePos = lineSide != null ? origin - (origin - lineSide.Level) / 2 : origin - 500
                });
            }
            return result;
        }
    }
}
