// New in B2M: level marks in sections and elevations, as the source drawings show them (PL, GL, floor levels).
// A target is an element (a floor, roof or other host object) or a level by name:
//   - element: a Revit spot elevation (Document.NewSpotElevation) on the element's flat top face, inset_mm in from the
//     element's end on the chosen side of the view; a label becomes the spot's own "Single/Upper Value Prefix"
//     (an instance parameter, so no type is edited); if that parameter cannot be set the result says label_applied false;
//   - level: with level_mode auto (default) a floor shown in the view whose top face is at the level's height (within
//     1 mm) hosts a spot elevation as above; when there is none, or level_mode is text, a text note (label, default the
//     level's name) sits on the level's height offset_mm beyond the building on the chosen side.
// Spot positions: the point is on the host face, found by projecting a point at the face's height in the view
// onto it; several positions across the host are tried when the first falls on a hole. Each target is its own
// transaction in one undo group, so a Revit warning drops only that target (reported).
// Text notes use a text type of text_size_mm found or copied from the default (never edited).

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

    public sealed class SpotLevelHandler : IRevitCommand
    {
        public string Name => "spot_level";
        public string Description =>
            "New in B2M: level marks in a section/elevation view (view_id, default the active view). targets: [{element_id | level_name, "
            + "label?}]. An element (floor, roof) gets a spot elevation on its top face, inset_mm (default 300) in from its end on side "
            + "(left|right, default left), label as the spot's prefix. A level gets a spot elevation on a floor at its height (level_mode "
            + "auto), else a text note (label, default the level name, e.g. PL / GL) offset_mm (default 500) beyond the building; level_mode "
            + "text always writes the note. Optional spot_type_id, has_leader, text_size_mm (default 2.5) or text_type_name.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""view_id"":{""type"":""integer""},
            ""targets"":{""type"":""array"",""items"":{""type"":""object""}},
            ""side"":{""type"":""string"",""enum"":[""left"",""right""]},
            ""offset_mm"":{""type"":""number""},
            ""inset_mm"":{""type"":""number""},
            ""level_mode"":{""type"":""string"",""enum"":[""auto"",""text""]},
            ""spot_type_id"":{""type"":""integer""},
            ""has_leader"":{""type"":""boolean""},
            ""text_size_mm"":{""type"":""number""},
            ""text_type_name"":{""type"":""string""}},
            ""required"":[""targets""]}";
        public bool ReadOnly => false;

        private sealed class Target
        {
            public int Index;
            public Element Host;
            public Level Level;
            public string Label;
        }

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!AnnotationHelpers.TryAnnotationView(doc, p.Value<long?>("view_id"), out View view, out var failure)) return failure;
            if (!SectionFrame.TryCreate(view, out var frame, out failure)) return failure;

            var sign = (p.Value<string>("side") ?? "left") == "right" ? 1 : -1;
            if (!DrawingSupport.TryNonNegative(p, "offset_mm", 500, out var offset, out failure)) return failure;
            if (!DrawingSupport.TryNonNegative(p, "inset_mm", 300, out var inset, out failure)) return failure;
            var levelMode = p.Value<string>("level_mode") ?? "auto";
            var hasLeader = p.Value<bool?>("has_leader") ?? false;

            SpotDimensionType spotType = null;
            var spotTypeId = p.Value<long?>("spot_type_id");
            if (spotTypeId.HasValue)
            {
                spotType = doc.GetElement(RevitCompat.ToElementId(spotTypeId.Value)) as SpotDimensionType;
                if (spotType == null || spotType.StyleType != DimensionStyleType.SpotElevation)
                    return CommandResult.Fail("TYPE_NOT_FOUND: no spot elevation type has id " + spotTypeId.Value + ".", new JObject
                    {
                        ["valid_spot_types"] = new JArray(new FilteredElementCollector(doc).OfClass(typeof(SpotDimensionType)).Cast<SpotDimensionType>()
                            .Where(t => t.StyleType == DimensionStyleType.SpotElevation).OrderBy(t => t.Name).Take(100)
                            .Select(t => new JObject { ["id"] = RevitCompat.GetId(t.Id), ["name"] = t.Name }))
                    });
            }

            // Targets.
            var items = (JArray)p["targets"];
            if (items.Count == 0) return CommandResult.Fail("INVALID_PARAMS: targets cannot be empty.");
            var allLevels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
            var targets = new List<Target>();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i] as JObject;
                var idToken = item?["element_id"];
                var levelToken = item?["level_name"];
                var labelToken = item?["label"];
                var shapeOk = item != null
                    && (idToken == null) != (levelToken == null)
                    && (idToken == null || idToken.Type == JTokenType.Integer)
                    && (levelToken == null || levelToken.Type == JTokenType.String)
                    && (labelToken == null || labelToken.Type == JTokenType.String || labelToken.Type == JTokenType.Null)
                    && item.Properties().All(x => x.Name == "element_id" || x.Name == "level_name" || x.Name == "label");
                if (!shapeOk)
                    return CommandResult.Fail("INVALID_PARAMS: targets[" + i + "] must be {\"element_id\": integer} or {\"level_name\": string}, with an optional \"label\": string.");
                var target = new Target { Index = i, Label = labelToken?.Type == JTokenType.String ? labelToken.Value<string>() : null };
                if (idToken != null)
                {
                    var host = doc.GetElement(RevitCompat.ToElementId(idToken.Value<long>())) as HostObject;
                    if (host == null)
                        return CommandResult.Fail("ELEMENT_NOT_FOUND: targets[" + i + "].element_id " + idToken.Value<long>()
                            + " is not a floor, roof or other host object with a top face.");
                    target.Host = host;
                }
                else
                {
                    if (!Lookup.TryLevel(doc, levelToken.Value<string>(), out var level, out failure)) return failure;
                    target.Level = level;
                }
                targets.Add(target);
            }

            DrawingSupport.TextTypePlan textPlan = null;
            if (targets.Any(t => t.Level != null)
                && !DrawingSupport.TryPlanTextType(doc, p.Value<double?>("text_size_mm") ?? 2.5, p.Value<string>("text_type_name"), out textPlan, out failure))
                return failure;

            var floors = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Floors).WhereElementIsNotElementType()
                .OfType<Floor>().ToList();
            var walls = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Wall)).Cast<Wall>().ToList();
            var extent = frame.ModelURange(floors.Cast<Element>().Concat(walls)) ?? frame.CropURange();

            var placed = new JArray();
            var skipped = new JArray();
            using (var group = new TransactionGroup(doc, "B2M: Spot Levels"))
            {
                group.Start();
                try
                {
                    TextNoteType textType = null;
                    if (textPlan != null)
                    {
                        var typeReason = DrawingSupport.RunItem(doc, "B2M: Text type", () => textType = DrawingSupport.ApplyTextType(textPlan));
                        if (typeReason != null)
                        {
                            group.RollBack();
                            return CommandResult.Fail("CREATE_FAILED: could not get the text type: " + typeReason + " Nothing was created.");
                        }
                    }

                    foreach (var target in targets)
                    {
                        var host = target.Host;
                        if (host == null && levelMode == "auto")
                            host = FloorAtLevel(floors, target.Level, frame, extent, sign);
                        var result = new JObject { ["index"] = target.Index };
                        if (target.Level != null) result["level_name"] = target.Level.Name;
                        string reason;
                        if (host != null)
                        {
                            SpotDimension spot = null;
                            bool? labelApplied = null;
                            string placeProblem = null;
                            reason = DrawingSupport.RunItem(doc, "B2M: Spot elevation", () =>
                            {
                                if (!TryHostPoint(host, view, frame, inset, sign, out var reference, out var point, out placeProblem))
                                    throw new InvalidOperationException(placeProblem);
                                var bend = point + frame.Up * DrawingSupport.ToFeet(view.Scale * 5.0) + frame.Right * (sign * DrawingSupport.ToFeet(view.Scale * 5.0));
                                var end = bend + frame.Right * (sign * DrawingSupport.ToFeet(view.Scale * 5.0));
                                spot = doc.Create.NewSpotElevation(view, reference, point, bend, end, point, hasLeader);
                                if (spot == null) throw new InvalidOperationException("Revit returned no spot elevation.");
                                if (spotType != null && spot.GetTypeId() != spotType.Id) spot.ChangeTypeId(spotType.Id);
                                if (!string.IsNullOrEmpty(target.Label))
                                {
                                    var prefix = spot.get_Parameter(BuiltInParameter.SPOT_ELEV_SINGLE_OR_UPPER_PREFIX);
                                    labelApplied = prefix != null && !prefix.IsReadOnly && prefix.Set(target.Label + " ");
                                }
                            });
                            if (reason == null)
                            {
                                result["kind"] = "spot_elevation";
                                result["element_id"] = RevitCompat.GetId(spot.Id);
                                result["host_id"] = RevitCompat.GetId(host.Id);
                                result["value_mm"] = spot.Value.HasValue ? Math.Round(DrawingSupport.ToMm(spot.Value.Value), 1) : (double?)null;
                                result["label"] = target.Label;
                                result["label_applied"] = labelApplied;
                                placed.Add(result);
                                continue;
                            }
                            if (target.Host != null)
                            {
                                result["reason"] = reason;
                                skipped.Add(result);
                                continue;
                            }
                            result["spot_problem"] = reason; // level target: fall back to the text mark
                        }

                        if (extent == null)
                        {
                            result["reason"] = "the view shows no walls or floors and has no active crop, so there is no side to put the mark on";
                            skipped.Add(result);
                            continue;
                        }
                        var text = string.IsNullOrEmpty(target.Label) ? target.Level.Name : target.Label;
                        TextNote note = null;
                        var u = SideMath.ChainLine(SideMath.OuterExtent(extent[0], extent[1], sign), sign, offset, 0, 0);
                        reason = DrawingSupport.RunItem(doc, "B2M: Level mark", () =>
                        {
                            var options = new TextNoteOptions(textType.Id)
                            {
                                HorizontalAlignment = sign > 0 ? HorizontalTextAlignment.Left : HorizontalTextAlignment.Right,
                                VerticalAlignment = VerticalTextAlignment.Bottom
                            };
                            note = TextNote.Create(doc, view.Id, frame.Point(u, DrawingSupport.ToMm(target.Level.ProjectElevation)), text, options);
                            if (note == null) throw new InvalidOperationException("Revit returned no text note.");
                        });
                        if (reason != null)
                        {
                            result["reason"] = reason;
                            skipped.Add(result);
                            continue;
                        }
                        result["kind"] = "text_note";
                        result["element_id"] = RevitCompat.GetId(note.Id);
                        result["text"] = text;
                        result["height_mm"] = Math.Round(DrawingSupport.ToMm(target.Level.ProjectElevation), 1);
                        placed.Add(result);
                    }
                    if (placed.Count == 0)
                    {
                        group.RollBack();
                        return CommandResult.Fail("NOTHING_PLACED: no level mark could be placed; see skipped. Nothing was created.", new JObject { ["skipped"] = skipped });
                    }
                    DrawingSupport.Finish(group, true);
                }
                catch (Exception ex)
                {
                    if (group.HasStarted() && !group.HasEnded()) group.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not place the level marks: " + ex.Message + " Nothing was created.");
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["view_name"] = view.Name,
                ["side"] = sign > 0 ? "right" : "left",
                ["text_type"] = textPlan?.ToJson(),
                ["element_ids"] = new JArray(placed.Select(x => x["element_id"])),
                ["placed"] = placed,
                ["skipped"] = skipped
            });
        }

        /// <summary>A floor in the view with a flat top face at the level's height, the one reaching furthest to the side.</summary>
        private static Floor FloorAtLevel(List<Floor> floors, Level level, SectionFrame frame, double[] extent, int sign)
        {
            var z = level.ProjectElevation;
            var tol = DrawingSupport.ToFeet(1);
            Floor best = null;
            var bestReach = double.MinValue;
            foreach (var floor in floors)
            {
                IList<Reference> tops;
                try { tops = HostObjectUtils.GetTopFaces(floor); }
                catch (Exception) { continue; }
                var atLevel = tops.Select(r => floor.GetGeometryObjectFromReference(r) as PlanarFace)
                    .Any(f => f != null && Math.Abs(f.FaceNormal.Z) > 0.9999 && Math.Abs(f.Origin.Z - z) <= tol);
                if (!atLevel) continue;
                var range = frame.URange(floor.get_BoundingBox(frame.View) ?? floor.get_BoundingBox(null));
                var reach = range == null ? double.MinValue : (sign > 0 ? range[1] : -range[0]);
                if (best == null || reach > bestReach)
                {
                    best = floor;
                    bestReach = reach;
                }
            }
            return best;
        }

        /// <summary>
        /// A point on the host's flat top face, seen inset mm in from its end on the side: a point at the face's height on
        /// the view plane, moved along the view direction to the host's middle, projected onto the face. Tries positions
        /// further in when the first falls on a hole or outside the face.
        /// </summary>
        private static bool TryHostPoint(Element host, View view, SectionFrame frame, double inset, int sign,
            out Reference reference, out XYZ point, out string problem)
        {
            reference = null;
            point = null;
            problem = null;
            IList<Reference> tops;
            try { tops = HostObjectUtils.GetTopFaces(host as HostObject); }
            catch (Exception ex) { problem = "no top face: " + ex.Message; return false; }
            var box = host.get_BoundingBox(view) ?? host.get_BoundingBox(null);
            var range = frame.URange(box);
            var crop = frame.CropURange();
            if (range != null && crop != null)
                range = new[] { Math.Max(range[0], crop[0]), Math.Min(range[1], crop[1]) };
            if (range == null || range[0] >= range[1])
            {
                problem = "the element is not inside the view's crop";
                return false;
            }
            var middle = box == null ? frame.Origin : (box.Transform ?? Transform.Identity).OfPoint((box.Min + box.Max) * 0.5);
            var depth = view.ViewDirection;
            var width = range[1] - range[0];
            var tries = new List<double> { Math.Min(inset, width / 2) };
            foreach (var f in new[] { 0.1, 0.25, 0.5, 0.75, 0.9 }) tries.Add(width * f);
            foreach (var r in tops)
            {
                if (!(host.GetGeometryObjectFromReference(r) is PlanarFace face) || Math.Abs(face.FaceNormal.Z) < 0.9999) continue;
                var zMm = DrawingSupport.ToMm(face.Origin.Z);
                foreach (var distance in tries)
                {
                    var u = sign > 0 ? range[1] - distance : range[0] + distance;
                    var onPlane = frame.Point(u, zMm);
                    var moved = onPlane + depth * (middle - onPlane).DotProduct(depth);
                    var hit = face.Project(moved);
                    if (hit == null) continue;
                    reference = r;
                    point = hit.XYZPoint;
                    return true;
                }
            }
            problem = "no point on a flat top face of the element could be found in this view";
            return false;
        }
    }
}
