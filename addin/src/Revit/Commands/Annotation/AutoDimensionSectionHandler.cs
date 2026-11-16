// New in B2M: vertical dimension chains in a section or elevation, as the source sections show them (2700 floor to
// floor, 150 slabs, 900 parapet, plinth and ground). References are live: level planes (Level.GetPlaneReference) and,
// for "slabs", the horizontal top and bottom faces of the floors in the view (HostObjectUtils.GetTopFaces /
// GetBottomFaces). Chains, nearest to the building first: "slabs" (levels plus slab faces, so a 2700 storey reads
// 2550 + 150), then "levels" (level to level). They stand offset_mm beyond the building's extent in the view on the
// chosen side (left/right of the view), spacing_mm apart. Levels at the same height merge; a slab face at a level's
// height merges into the level. Each chain is its own transaction in one undo group (a Revit warning rolls back only
// that chain and it is reported in skipped).
// Limits: parapet, plinth and ground show only if they exist as levels (or as floors for "slabs"); beams are not
// dimensioned; sloped floor faces are ignored.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using B2M.Revit.Core.Drawing;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Annotation
{
    // B2M.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class AutoDimensionSectionHandler : IRevitCommand
    {
        private static readonly string[] IncludeNames = { "slabs", "levels" };

        public string Name => "auto_dimension_section";
        public string Description =>
            "New in B2M: vertical dimension chains in a section/elevation view (view_id, default the active view). include: levels "
            + "(level to level) and/or slabs (levels plus floor top/bottom faces, e.g. 2550 + 150); default levels. side: left or right "
            + "of the view (default left); offset_mm (default 800) beyond the building, spacing_mm (default 600) between chains. "
            + "level_names limits the levels used. Returns the dimension ids, stops and Revit's segment values, and anything skipped.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""view_id"":{""type"":""integer""},
            ""side"":{""type"":""string"",""enum"":[""left"",""right""]},
            ""offset_mm"":{""type"":""number""},
            ""spacing_mm"":{""type"":""number""},
            ""include"":{""type"":""array"",""items"":{""type"":""string""}},
            ""level_names"":{""type"":""array"",""items"":{""type"":""string""}},
            ""merge_tolerance_mm"":{""type"":""number""},
            ""dimension_type_id"":{""type"":""integer""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!AnnotationHelpers.TryAnnotationView(doc, p.Value<long?>("view_id"), out View view, out var failure)) return failure;
            if (!SectionFrame.TryCreate(view, out var frame, out failure)) return failure;

            var sign = (p.Value<string>("side") ?? "left") == "right" ? 1 : -1;
            if (!DrawingSupport.TryStringSet(p, "include", IncludeNames, new[] { "levels" }, out var include, out failure)) return failure;
            if (!DrawingSupport.TryNonNegative(p, "offset_mm", 800, out var offset, out failure)) return failure;
            if (!DrawingSupport.TryNonNegative(p, "spacing_mm", 600, out var spacing, out failure)) return failure;
            if (!DrawingSupport.TryNonNegative(p, "merge_tolerance_mm", 1, out var tol, out failure)) return failure;
            if (tol < 0.1) return CommandResult.Fail("INVALID_PARAMS: merge_tolerance_mm must be at least 0.1.");
            if (include.Count > 1 && spacing < 1) return CommandResult.Fail("INVALID_PARAMS: spacing_mm must be at least 1 so chains do not overlap.");
            if (!DrawingSupport.TryDimensionType(doc, p.Value<long?>("dimension_type_id"), out var dimType, out failure)) return failure;

            var skipped = new JArray();
            var refs = new List<Reference>();

            // Levels shown in the view, optionally filtered by name.
            var levels = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
            if (p["level_names"] is JArray names)
            {
                var wanted = names.Select(n => n.Value<string>()).ToList();
                var missing = wanted.Where(n => levels.All(l => !string.Equals(l.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
                if (missing.Count > 0)
                    return CommandResult.Fail("LEVEL_NOT_FOUND: level(s) " + string.Join(", ", missing.Select(m => "'" + m + "'"))
                        + " are not shown in view '" + view.Name + "'.", new JObject { ["valid_levels"] = new JArray(levels.Select(l => l.Name)) });
                levels = levels.Where(l => wanted.Any(n => string.Equals(l.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
            }
            var levelStops = new List<ChainStop>();
            foreach (var level in levels)
            {
                Reference reference;
                try { reference = level.GetPlaneReference(); }
                catch (Exception) { reference = null; }
                if (reference == null)
                {
                    skipped.Add(new JObject { ["level"] = level.Name, ["reason"] = "Revit gives no plane reference for this level" });
                    continue;
                }
                refs.Add(reference);
                levelStops.Add(new ChainStop(DrawingSupport.ToMm(level.ProjectElevation), 0, "level " + level.Name, refs.Count - 1));
            }

            // Floors in the view: used for the building's extent and, with "slabs", their horizontal faces.
            var floors = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Floors).WhereElementIsNotElementType()
                .OfType<Floor>().ToList();
            var walls = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Wall)).Cast<Wall>().ToList();
            var extent = frame.ModelURange(floors.Cast<Element>().Concat(walls));
            if (extent == null) extent = frame.CropURange();
            if (extent == null)
                return CommandResult.Fail("NO_EXTENT: view '" + view.Name + "' shows no walls or floors and has no active crop, so there is no side to dimension on.");
            var outer = SideMath.OuterExtent(extent[0], extent[1], sign);

            var slabStops = new List<ChainStop>();
            if (include.Contains("slabs"))
            {
                foreach (var floor in floors)
                {
                    var range = frame.URange(floor.get_BoundingBox(view) ?? floor.get_BoundingBox(null));
                    // Nearer to the chain's side ranks better when two floors give the same height.
                    var distance = range == null ? 1e9 : Math.Abs(outer - (sign > 0 ? range[1] : range[0]));
                    var priority = 1 + (int)Math.Min(1e6, distance / 100);
                    var floorRefs = new List<Tuple<Reference, string>>();
                    try
                    {
                        floorRefs.AddRange(HostObjectUtils.GetTopFaces(floor).Select(r => Tuple.Create(r, "top")));
                        floorRefs.AddRange(HostObjectUtils.GetBottomFaces(floor).Select(r => Tuple.Create(r, "bottom")));
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(new JObject { ["element_id"] = RevitCompat.GetId(floor.Id), ["reason"] = "no faces: " + ex.Message });
                        continue;
                    }
                    foreach (var fr in floorRefs)
                    {
                        if (!(floor.GetGeometryObjectFromReference(fr.Item1) is PlanarFace face) || Math.Abs(face.FaceNormal.Z) < 0.9999)
                        {
                            skipped.Add(new JObject { ["element_id"] = RevitCompat.GetId(floor.Id), ["reason"] = "the " + fr.Item2 + " face is not flat and horizontal" });
                            continue;
                        }
                        refs.Add(fr.Item1);
                        slabStops.Add(new ChainStop(DrawingSupport.ToMm(face.Origin.Z), priority,
                            "floor " + RevitCompat.GetId(floor.Id) + " " + fr.Item2, refs.Count - 1));
                    }
                }
            }

            var planned = new List<Tuple<string, ChainBuildResult>>();
            foreach (var mode in IncludeNames)
            {
                if (!include.Contains(mode)) continue;
                var chain = ChainBuilder.Build(mode == "slabs" ? levelStops.Concat(slabStops) : levelStops, tol);
                if (chain.SkipReason != null)
                {
                    skipped.Add(new JObject { ["mode"] = mode, ["reason"] = chain.SkipReason });
                    continue;
                }
                var same = planned.FirstOrDefault(c => ChainBuilder.SameChain(c.Item2.Positions, chain.Positions, tol));
                if (same != null)
                {
                    skipped.Add(new JObject { ["mode"] = mode, ["reason"] = "same stops as the " + same.Item1 + " chain" });
                    continue;
                }
                planned.Add(Tuple.Create(mode, chain));
            }
            if (planned.Count == 0)
                return CommandResult.Fail("NOTHING_TO_DIMENSION: no chain could be planned in view '" + view.Name + "'; see skipped.",
                    new JObject { ["skipped"] = skipped });

            var created = new JArray();
            using (var group = new TransactionGroup(doc, "B2M: Auto Dimension Section"))
            {
                group.Start();
                try
                {
                    for (var i = 0; i < planned.Count; i++)
                    {
                        var mode = planned[i].Item1;
                        var chain = planned[i].Item2;
                        var u = SideMath.ChainLine(outer, sign, offset, spacing, i);
                        var line = Line.CreateBound(frame.Point(u, chain.Stops[0].Position), frame.Point(u, chain.Stops[chain.Stops.Count - 1].Position));
                        var array = new ReferenceArray();
                        foreach (var s in chain.Stops) array.Append(refs[s.Key]);
                        Dimension dim = null;
                        var reason = DrawingSupport.RunItem(doc, "B2M: Dimension chain", () =>
                        {
                            dim = dimType != null ? doc.Create.NewDimension(view, line, array, dimType) : doc.Create.NewDimension(view, line, array);
                            if (dim == null) throw new InvalidOperationException("Revit returned no dimension.");
                        });
                        if (reason != null)
                        {
                            skipped.Add(new JObject { ["mode"] = mode, ["reason"] = "Revit refused the chain: " + reason });
                            continue;
                        }
                        created.Add(new JObject
                        {
                            ["dimension_id"] = RevitCompat.GetId(dim.Id),
                            ["mode"] = mode,
                            ["u_mm"] = Math.Round(u, 1),
                            ["stops"] = DrawingSupport.Stops(chain.Stops),
                            ["planned_segments_mm"] = DrawingSupport.Segments(chain),
                            ["values_mm"] = DrawingSupport.Values(dim),
                            ["merged_stops"] = DrawingSupport.Merged(chain.Merged)
                        });
                    }
                    DrawingSupport.Finish(group, created.Count > 0);
                }
                catch (Exception ex)
                {
                    if (group.HasStarted() && !group.HasEnded()) group.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not dimension the section: " + ex.Message + " Nothing was created.");
                }
            }

            if (created.Count == 0)
                return CommandResult.Fail("CREATE_FAILED: Revit created none of the planned chains; see skipped. Nothing was created.",
                    new JObject { ["skipped"] = skipped });
            return CommandResult.Ok(new JObject
            {
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["view_name"] = view.Name,
                ["side"] = sign > 0 ? "right" : "left",
                ["dimension_ids"] = new JArray(created.Select(d => d["dimension_id"])),
                ["dimensions"] = created,
                ["skipped"] = skipped
            });
        }
    }
}
