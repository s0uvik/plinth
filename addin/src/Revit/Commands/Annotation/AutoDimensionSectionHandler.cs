// New in Plinth: vertical dimension chains in a section or elevation, as the source sections show them (2700 floor to
// floor, 150 slabs, 900 parapet, plinth and ground). References are live: level planes (Level.GetPlaneReference) and,
// for "slabs", the horizontal top and bottom faces of the floors in the view (HostObjectUtils.GetTopFaces /
// GetBottomFaces). Chains, nearest to the building first: "slabs" (levels plus slab faces, so a 2700 storey reads
// 2550 + 150), then "levels" (level to level). They stand offset_mm beyond the building's extent in the view on the
// chosen side (left/right of the view), spacing_mm apart. Levels at the same height merge; a slab face at a level's
// height merges into the level. Each chain is its own transaction in one undo group (a Revit warning rolls back only
// that chain and it is reported in skipped).
// Step 9 T10b adds three chains (all ordered nearest the building first: openings, beams, slabs, foundation, levels):
//   - "openings": levels plus the sill and head of every door/window in the view (cut or seen), from the instances'
//     Bottom/Top family references, else their outermost bottom/top geometry faces (reported per opening in
//     opening_references, with sill/head heights relative to the opening's level);
//   - "beams": levels plus the top and bottom faces of the structural framing the section's cut plane passes through
//     (geometry faces; a family instance's symbol faces become instance references), so a storey reads e.g. 2250 + 450;
//   - "foundation": the ground level (ground_level, default the level nearest elevation 0) and the levels below it,
//     plus the top/bottom faces of the structural foundations in the view (slabs, footings, wall footings) and of the
//     floors below ground level (PCC, soling: each modelled layer is its own floor).
// Limits: parapet, plinth and ground show only if they exist as levels (or as floors for "slabs"); sloped faces are
// ignored; the layer boundaries inside one compound floor are not faces and are reported, not dimensioned; framing
// shown as stick lines (coarse detail) offers no faces in the view, so Revit refuses those chains (reported);
// nested family geometry is not used.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Plinth.Revit.Core.Drawing;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Annotation
{
    // Plinth.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class AutoDimensionSectionHandler : IRevitCommand
    {
        private static readonly string[] IncludeNames = { "slabs", "levels", "beams", "foundation", "openings" };

        public string Name => "auto_dimension_section";
        public string Description =>
            "New in Plinth: vertical dimension chains in a section/elevation view (view_id, default the active view). include: levels "
            + "(level to level), slabs (levels plus floor top/bottom faces, e.g. 2550 + 150), beams (levels plus top/bottom faces of "
            + "structural framing the section cuts, e.g. 2250 + 450), foundation (ground level and levels below it plus foundation and "
            + "below-ground floor top/bottom faces; ground_level names the ground level, default the level nearest 0), openings "
            + "(levels plus door/window sills and heads); default levels. Chains go nearest first: openings, beams, slabs, foundation, "
            + "levels. side: left or right of the view (default left); offset_mm (default 800) beyond the building, spacing_mm "
            + "(default 600) between chains. level_names limits the levels used. Returns the dimension ids, stops and Revit's segment "
            + "values, and anything skipped.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""view_id"":{""type"":""integer""},
            ""side"":{""type"":""string"",""enum"":[""left"",""right""]},
            ""offset_mm"":{""type"":""number""},
            ""spacing_mm"":{""type"":""number""},
            ""include"":{""type"":""array"",""items"":{""type"":""string""}},
            ""level_names"":{""type"":""array"",""items"":{""type"":""string""}},
            ""ground_level"":{""type"":""string""},
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
            if (p["ground_level"] != null && !include.Contains("foundation"))
                return CommandResult.Fail("INVALID_PARAMS: ground_level goes with include \"foundation\".");

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

            // The ground level (foundation chain): named, else the project's level nearest elevation 0.
            Level ground = null;
            if (include.Contains("foundation"))
            {
                var all = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
                var groundName = p.Value<string>("ground_level");
                if (!string.IsNullOrEmpty(groundName))
                {
                    ground = all.FirstOrDefault(l => string.Equals(l.Name, groundName, StringComparison.OrdinalIgnoreCase));
                    if (ground == null)
                        return CommandResult.Fail("LEVEL_NOT_FOUND: no level named '" + groundName + "' for ground_level.",
                            new JObject { ["valid_levels"] = new JArray(all.Select(l => l.Name)) });
                }
                else
                {
                    ground = all.OrderBy(l => Math.Abs(l.ProjectElevation)).ThenBy(l => l.Name).FirstOrDefault();
                    if (ground == null) return CommandResult.Fail("NO_LEVELS: the project has no levels, so there is no ground level.");
                }
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
            var beams = include.Contains("beams") ? CutFraming(doc, view, tol) : new List<Element>();
            var foundations = include.Contains("foundation")
                ? new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_StructuralFoundation).WhereElementIsNotElementType().ToList()
                : new List<Element>();
            var extent = frame.ModelURange(floors.Cast<Element>().Concat(walls).Concat(beams).Concat(foundations));
            if (extent == null) extent = frame.CropURange();
            if (extent == null)
                return CommandResult.Fail("NO_EXTENT: view '" + view.Name + "' shows no walls or floors and has no active crop, so there is no side to dimension on.");
            var outer = SideMath.OuterExtent(extent[0], extent[1], sign);

            // Nearer to the chain's side ranks better when two elements give the same height.
            int Priority(Element e)
            {
                var range = frame.URange(e.get_BoundingBox(view) ?? e.get_BoundingBox(null));
                var distance = range == null ? 1e9 : Math.Abs(outer - (sign > 0 ? range[1] : range[0]));
                return 1 + (int)Math.Min(1e6, distance / 100);
            }

            var slabStops = new List<ChainStop>();
            if (include.Contains("slabs"))
                foreach (var floor in floors) AddHostFaces(floor, "floor", Priority(floor), refs, slabStops, skipped);

            var beamStops = new List<ChainStop>();
            if (include.Contains("beams"))
            {
                if (beams.Count == 0) skipped.Add(new JObject { ["mode"] = "beams", ["reason"] = "the view's cut plane passes through no structural framing" });
                foreach (var beam in beams) AddTopBottomFaces(doc, view, beam, "beam", Priority(beam), refs, beamStops, skipped);
            }

            var foundationStops = new List<ChainStop>();
            JObject groundReport = null;
            if (ground != null)
            {
                var groundMm = DrawingSupport.ToMm(ground.ProjectElevation);
                groundReport = new JObject { ["name"] = ground.Name, ["elevation_mm"] = Math.Round(groundMm, 1) };
                if (levels.All(l => l.Id != ground.Id))
                    skipped.Add(new JObject { ["mode"] = "foundation", ["level"] = ground.Name,
                        ["reason"] = "the ground level is not shown in this view (or not in level_names), so the chain does not stop at it" });
                foundationStops.AddRange(levelStops.Where(s => s.Position <= groundMm + tol));
                if (foundations.Count == 0)
                    skipped.Add(new JObject { ["mode"] = "foundation", ["reason"] = "the view shows no structural foundations" });
                foreach (var f in foundations)
                {
                    if (f is HostObject host) AddHostFaces(host, "foundation", Priority(f), refs, foundationStops, skipped);
                    else AddTopBottomFaces(doc, view, f, "foundation", Priority(f), refs, foundationStops, skipped);
                }
                foreach (var floor in floors)
                {
                    var box = floor.get_BoundingBox(null);
                    if (box == null || DrawingSupport.ToMm(box.Max.Z) > groundMm + tol) continue;
                    AddHostFaces(floor, "floor", Priority(floor), refs, foundationStops, skipped);
                    var layers = LayerCount(doc, floor);
                    if (layers > 1)
                        skipped.Add(new JObject { ["mode"] = "foundation", ["element_id"] = RevitCompat.GetId(floor.Id),
                            ["reason"] = "floor has " + layers + " layers; the boundaries inside it are not faces, so only its top and bottom are dimensioned" });
                }
            }

            var openingStops = new List<ChainStop>();
            var openingReport = new JArray();
            if (include.Contains("openings"))
            {
                var openings = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType()
                    .WherePasses(new ElementMulticategoryFilter(new[] { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows }))
                    .OfType<FamilyInstance>().ToList();
                if (openings.Count == 0) skipped.Add(new JObject { ["mode"] = "openings", ["reason"] = "the view shows no doors or windows" });
                var levelElevations = levels.Select(l => DrawingSupport.ToMm(l.ProjectElevation)).ToList();
                foreach (var opening in openings)
                    AddOpening(doc, view, opening, levels, levelElevations, tol, Priority(opening), refs, openingStops, openingReport, skipped);
            }

            var planned = new List<Tuple<string, ChainBuildResult>>();
            foreach (var mode in SectionChains.Ordered(include))
            {
                IEnumerable<ChainStop> stops;
                switch (mode)
                {
                    case "slabs": stops = levelStops.Concat(slabStops); break;
                    case "beams": stops = levelStops.Concat(beamStops); break;
                    case "foundation": stops = foundationStops; break;
                    case "openings": stops = levelStops.Concat(openingStops); break;
                    default: stops = levelStops; break;
                }
                // A new chain with none of its own stops would only repeat the levels chain.
                var own = mode == "beams" ? beamStops : mode == "openings" ? openingStops
                    : mode == "foundation" ? foundationStops.Where(s => !levelStops.Contains(s)).ToList() : null;
                if (own != null && own.Count == 0)
                {
                    skipped.Add(new JObject { ["mode"] = mode, ["reason"] = "no " + mode + " faces or references found; see the other skipped entries" });
                    continue;
                }
                var chain = ChainBuilder.Build(stops, tol);
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
            using (var group = new TransactionGroup(doc, "Plinth: Auto Dimension Section"))
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
                        var reason = DrawingSupport.RunItem(doc, "Plinth: Dimension chain", () =>
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
            var result = new JObject
            {
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["view_name"] = view.Name,
                ["side"] = sign > 0 ? "right" : "left",
                ["dimension_ids"] = new JArray(created.Select(d => d["dimension_id"])),
                ["dimensions"] = created,
                ["skipped"] = skipped
            };
            if (groundReport != null) result["ground_level"] = groundReport;
            if (include.Contains("openings")) result["opening_references"] = openingReport;
            return CommandResult.Ok(result);
        }

        /// <summary>Flat horizontal top and bottom faces of a floor or foundation slab (HostObjectUtils), as stops.</summary>
        private static void AddHostFaces(HostObject host, string what, int priority, List<Reference> refs, List<ChainStop> stops, JArray skipped)
        {
            var hostRefs = new List<Tuple<Reference, string>>();
            try
            {
                hostRefs.AddRange(HostObjectUtils.GetTopFaces(host).Select(r => Tuple.Create(r, "top")));
                hostRefs.AddRange(HostObjectUtils.GetBottomFaces(host).Select(r => Tuple.Create(r, "bottom")));
            }
            catch (Exception ex)
            {
                skipped.Add(new JObject { ["element_id"] = RevitCompat.GetId(host.Id), ["reason"] = "no faces: " + ex.Message });
                return;
            }
            foreach (var fr in hostRefs)
            {
                if (!(host.GetGeometryObjectFromReference(fr.Item1) is PlanarFace face) || Math.Abs(face.FaceNormal.Z) < 0.9999)
                {
                    skipped.Add(new JObject { ["element_id"] = RevitCompat.GetId(host.Id), ["reason"] = "the " + fr.Item2 + " face is not flat and horizontal" });
                    continue;
                }
                refs.Add(fr.Item1);
                stops.Add(new ChainStop(DrawingSupport.ToMm(face.Origin.Z), priority, what + " " + RevitCompat.GetId(host.Id) + " " + fr.Item2, refs.Count - 1));
            }
        }

        /// <summary>The outermost flat top (+Z) and bottom (-Z) geometry faces of an element (beams, footings), as stops.</summary>
        private static void AddTopBottomFaces(Document doc, View view, Element element, string what, int priority,
            List<Reference> refs, List<ChainStop> stops, JArray skipped)
        {
            foreach (var top in new[] { true, false })
            {
                var faces = ReferenceSupport.DirectionalFaces(doc, element, view, XYZ.BasisZ, top);
                var name = top ? "top" : "bottom";
                if (faces.Count == 0)
                {
                    skipped.Add(new JObject { ["element_id"] = RevitCompat.GetId(element.Id), ["reason"] = what + " has no flat horizontal " + name + " face with a reference" });
                    continue;
                }
                refs.Add(faces[0].Reference);
                stops.Add(new ChainStop(DrawingSupport.ToMm(faces[0].Origin.Z), priority, what + " " + RevitCompat.GetId(element.Id) + " " + name, refs.Count - 1));
            }
        }

        /// <summary>Structural framing whose model bounding box the view's cut plane passes through.</summary>
        private static List<Element> CutFraming(Document doc, View view, double tolMm)
        {
            var box = view.CropBox;
            var t = box.Transform;
            var planePoint = t.OfPoint(new XYZ(0, 0, box.Max.Z));
            var normal = t.BasisZ;
            var result = new List<Element>();
            foreach (var e in new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_StructuralFraming).WhereElementIsNotElementType())
            {
                var b = e.get_BoundingBox(null);
                if (b == null) continue;
                var bt = b.Transform ?? Transform.Identity;
                var distances = new List<double>();
                foreach (var x in new[] { b.Min.X, b.Max.X })
                    foreach (var y in new[] { b.Min.Y, b.Max.Y })
                        foreach (var z in new[] { b.Min.Z, b.Max.Z })
                            distances.Add(DrawingSupport.ToMm((bt.OfPoint(new XYZ(x, y, z)) - planePoint).DotProduct(normal)));
                if (SectionChains.IsCut(distances, tolMm)) result.Add(e);
            }
            return result;
        }

        private static int LayerCount(Document doc, Floor floor)
        {
            try
            {
                var structure = (doc.GetElement(floor.GetTypeId()) as FloorType)?.GetCompoundStructure();
                return structure == null ? 0 : structure.GetLayers().Count(l => l.Width > 1e-9);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// A door or window's sill (Bottom reference) and head (Top reference) as stops; where a family reference is
        /// missing, its outermost bottom/top geometry face. Heights come from the face, else the sill/head parameters on
        /// the opening's level, else its bounding box.
        /// </summary>
        private static void AddOpening(Document doc, View view, FamilyInstance opening, List<Level> levels, List<double> levelElevations,
            double tol, int priority, List<Reference> refs, List<ChainStop> stops, JArray report, JArray skipped)
        {
            var id = RevitCompat.GetId(opening.Id);
            var what = (opening.Category?.BuiltInCategory == BuiltInCategory.OST_Doors ? "door " : "window ") + id;
            var box = opening.get_BoundingBox(null);
            var level = doc.GetElement(opening.LevelId) as Level;
            if (level == null && box != null)
            {
                var index = SectionChains.LevelBelow(levelElevations, DrawingSupport.ToMm(box.Min.Z), tol);
                if (index >= 0) level = levels[index];
            }
            var levelMm = level == null ? (double?)null : DrawingSupport.ToMm(level.ProjectElevation);
            var entry = new JObject
            {
                ["element_id"] = id,
                ["kind"] = what.Split(' ')[0],
                ["level"] = level?.Name
            };
            foreach (var top in new[] { false, true })
            {
                var name = top ? "head" : "sill";
                Reference reference = opening.GetReferences(top ? FamilyInstanceReferenceType.Top : FamilyInstanceReferenceType.Bottom).FirstOrDefault();
                double? position = null;
                string via;
                if (reference != null)
                {
                    via = "family reference " + (top ? "Top" : "Bottom");
                    var param = opening.get_Parameter(top ? BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM : BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                    if (levelMm.HasValue && param != null && param.HasValue && param.StorageType == StorageType.Double)
                        position = levelMm.Value + DrawingSupport.ToMm(param.AsDouble());
                    else if (box != null)
                        position = DrawingSupport.ToMm(top ? box.Max.Z : box.Min.Z);
                }
                else
                {
                    var faces = ReferenceSupport.DirectionalFaces(doc, opening, view, XYZ.BasisZ, top);
                    if (faces.Count > 0)
                    {
                        reference = faces[0].Reference;
                        position = DrawingSupport.ToMm(faces[0].Origin.Z);
                    }
                    via = reference == null ? null : "geometry face (" + (top ? "top" : "bottom") + " of the bounding geometry; no " + (top ? "Top" : "Bottom") + " family reference)";
                }
                if (reference == null || !position.HasValue)
                {
                    entry[name + "_via"] = null;
                    skipped.Add(new JObject { ["mode"] = "openings", ["element_id"] = id,
                        ["reason"] = what + ": no " + name + " reference (no " + (top ? "Top" : "Bottom") + " family reference and no flat face)" });
                    continue;
                }
                refs.Add(reference);
                stops.Add(new ChainStop(position.Value, priority, what + " " + name, refs.Count - 1));
                entry[name + "_via"] = via;
                entry[name + "_mm"] = levelMm.HasValue ? Math.Round(position.Value - levelMm.Value, 1) : (double?)null;
            }
            report.Add(entry);
        }
    }
}
