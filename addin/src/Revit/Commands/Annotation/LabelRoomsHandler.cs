// New in Plinth: room labels as the source plans show them, "BED ROOM" over "3500 X 4000". The Revit API cannot make a
// label family, so each room gets an existing room tag (its name; the project's default room tag type, or
// tag_type_id / tag_type_name) and a text note hung under the tag: centred on the tag, its top gap_mm (paper) below the
// tag's box, which is read from Revit after the tag is placed (the view scale turns paper mm into model mm). The text is
// given per room, or measured: the room's bounding rectangle (finish boundary, model X/Y) rounded to round_mm and
// written "<a> X <b>" in size_order. The note's text type is an existing one of text_size_mm (or text_type_name), else
// a copy of the default text type made for that size; an existing type is never edited.
// Each room is its own transaction inside one undo group, so a Revit warning drops only that room (reported). Tag and
// note are not grouped: Revit keeps a room tag out of detail groups unless its room is grouped too, so the per-room ids
// are returned for the service to record and remove.

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

    public sealed class LabelRoomsHandler : IRevitCommand
    {
        public string Name => "label_rooms";
        public string Description =>
            "New in Plinth: label rooms in a plan view (view_id, default the active view) with a room tag (name) and a text note under it "
            + "such as \"3500 X 4000\". rooms: [{room_id, text?}] (default every placed room in the view). text_mode: measured (default; "
            + "the room's bounding rectangle rounded to round_mm, default 5, ordered by size_order x_first|longer_first|shorter_first) or "
            + "given (each room needs text). A room's own text always wins. text_size_mm (default 2.5) picks or makes the note's text type "
            + "(an existing type is never edited); text_type_name uses that type instead. gap_mm (paper, default 1) under the tag. "
            + "skip_tagged (default true) leaves rooms already tagged in the view alone; false reuses their tag. Returns tag and note ids per room.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""view_id"":{""type"":""integer""},
            ""rooms"":{""type"":""array"",""items"":{""type"":""object""}},
            ""text_mode"":{""type"":""string"",""enum"":[""measured"",""given""]},
            ""size_order"":{""type"":""string"",""enum"":[""x_first"",""longer_first"",""shorter_first""]},
            ""round_mm"":{""type"":""number""},
            ""separator"":{""type"":""string""},
            ""text_size_mm"":{""type"":""number""},
            ""text_type_name"":{""type"":""string""},
            ""gap_mm"":{""type"":""number""},
            ""tag_type_id"":{""type"":""integer""},
            ""tag_type_name"":{""type"":""string""},
            ""tag_family_name"":{""type"":""string""},
            ""skip_tagged"":{""type"":""boolean""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!AnnotationHelpers.TryAnnotationView(doc, p.Value<long?>("view_id"), out View view, out var failure)) return failure;
            if (!(view is ViewPlan))
                return CommandResult.Fail("NOT_A_PLAN_VIEW: room tags need a plan view; '" + view.Name + "' is a " + view.ViewType + ".");

            var textMode = p.Value<string>("text_mode") ?? "measured";
            var order = p.Value<string>("size_order") ?? "x_first";
            var separator = p.Value<string>("separator") ?? " X ";
            if (!DrawingSupport.TryNonNegative(p, "round_mm", 5, out var roundMm, out failure)) return failure;
            if (!DrawingSupport.TryNonNegative(p, "gap_mm", 1, out var gapMm, out failure)) return failure;
            var textSize = p.Value<double?>("text_size_mm") ?? 2.5;
            var skipTagged = p.Value<bool?>("skip_tagged") ?? true;

            // Tag type: given, else the project's default room tag (a room tag family must be loaded).
            FamilySymbol tagType = null;
            var typeId = p.Value<long?>("tag_type_id");
            var typeName = p.Value<string>("tag_type_name");
            var familyName = p.Value<string>("tag_family_name");
            if (!string.IsNullOrEmpty(familyName) && string.IsNullOrEmpty(typeName))
                return CommandResult.Fail("INVALID_PARAMS: tag_family_name needs tag_type_name.");
            if (typeId.HasValue && !string.IsNullOrEmpty(typeName))
                return CommandResult.Fail("INVALID_PARAMS: pass tag_type_id or tag_type_name, not both.");
            if (typeId.HasValue || !string.IsNullOrEmpty(typeName))
            {
                if (!Lookup.TryFamilySymbol(doc, typeId, familyName, typeName, BuiltInCategory.OST_RoomTags, out tagType, out failure)) return failure;
                if (tagType.Category?.BuiltInCategory != BuiltInCategory.OST_RoomTags)
                    return CommandResult.Fail("TYPE_NOT_FOUND: type " + RevitCompat.GetId(tagType.Id) + " is not a room tag type.");
            }
            else if (new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_RoomTags).FirstElement() == null)
            {
                return CommandResult.Fail("NO_ROOM_TAG_TYPE: no room tag family is loaded in the project.");
            }

            if (!DrawingSupport.TryPlanTextType(doc, textSize, p.Value<string>("text_type_name"), out var textPlan, out failure)) return failure;

            // Rooms and their texts.
            var inView = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<Room>().Where(r => r.Area > 0 && r.Location is LocationPoint).ToList();
            var jobs = new List<Tuple<Room, string>>();
            if (p["rooms"] is JArray items)
            {
                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i] as JObject;
                    var idToken = item?["room_id"];
                    var textToken = item?["text"];
                    if (item == null || idToken == null || idToken.Type != JTokenType.Integer
                        || (textToken != null && textToken.Type != JTokenType.String && textToken.Type != JTokenType.Null)
                        || item.Properties().Any(x => x.Name != "room_id" && x.Name != "text"))
                        return CommandResult.Fail("INVALID_PARAMS: rooms[" + i + "] must be {\"room_id\": integer, \"text\"?: string}.");
                    var room = inView.FirstOrDefault(r => RevitCompat.GetId(r.Id) == idToken.Value<long>());
                    if (room == null)
                        return CommandResult.Fail("ROOM_NOT_FOUND: rooms[" + i + "].room_id " + idToken.Value<long>() + " is not a placed room shown in view '" + view.Name + "'.",
                            new JObject { ["valid_room_ids"] = new JArray(inView.Take(200).Select(r => RevitCompat.GetId(r.Id))) });
                    if (jobs.Any(j => j.Item1.Id == room.Id))
                        return CommandResult.Fail("INVALID_PARAMS: room " + idToken.Value<long>() + " is listed twice.");
                    jobs.Add(Tuple.Create(room, textToken?.Type == JTokenType.String ? textToken.Value<string>() : null));
                }
            }
            else
            {
                jobs.AddRange(inView.Select(r => Tuple.Create(r, (string)null)));
            }
            if (jobs.Count == 0)
                return CommandResult.Fail("NO_ROOMS: view '" + view.Name + "' shows no placed rooms.");
            var missingText = jobs.Where(j => textMode == "given" && string.IsNullOrEmpty(j.Item2)).Select(j => RevitCompat.GetId(j.Item1.Id)).ToList();
            if (missingText.Count > 0)
                return CommandResult.Fail("INVALID_PARAMS: text_mode given needs a text for every room; missing for room(s) " + string.Join(", ", missingText) + ".");

            var existingTags = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsNotElementType()
                .OfType<RoomTag>().Where(t => !t.IsTaggingLink && t.Room != null)
                .GroupBy(t => RevitCompat.GetId(t.Room.Id)).ToDictionary(g => g.Key, g => g.First());

            var labelled = new JArray();
            var skipped = new JArray();
            using (var group = new TransactionGroup(doc, "Plinth: Label Rooms"))
            {
                group.Start();
                try
                {
                    TextNoteType textType = null;
                    var typeReason = DrawingSupport.RunItem(doc, "Plinth: Text type", () => textType = DrawingSupport.ApplyTextType(textPlan));
                    if (typeReason != null)
                    {
                        group.RollBack();
                        return CommandResult.Fail("CREATE_FAILED: could not get the note's text type: " + typeReason + " Nothing was created.");
                    }

                    foreach (var job in jobs)
                    {
                        var room = job.Item1;
                        var roomId = RevitCompat.GetId(room.Id);
                        existingTags.TryGetValue(roomId, out var existing);
                        if (existing != null && skipTagged)
                        {
                            skipped.Add(new JObject { ["room_id"] = roomId, ["reason"] = "already tagged in this view (tag " + RevitCompat.GetId(existing.Id) + "); pass skip_tagged false to reuse it" });
                            continue;
                        }
                        var text = job.Item2;
                        double[] size = null;
                        if (string.IsNullOrEmpty(text))
                        {
                            size = MeasuredSize(room);
                            if (size == null)
                            {
                                skipped.Add(new JObject { ["room_id"] = roomId, ["reason"] = "the room has no boundary to measure" });
                                continue;
                            }
                            text = RoomSizeText.Format(size[0], size[1], order, roundMm, separator);
                        }

                        Element tag = existing;
                        TextNote note = null;
                        var reason = DrawingSupport.RunItem(doc, "Plinth: Label room", () =>
                        {
                            if (tag == null)
                            {
                                var point = ((LocationPoint)room.Location).Point;
                                tag = AnnotationHelpers.CreateTag(doc, view, room, tagType, false, TagOrientation.Horizontal, point);
                            }
                            doc.Regenerate();
                            var head = AnnotationHelpers.HeadPosition(tag) ?? ((LocationPoint)room.Location).Point;
                            var box = tag.get_BoundingBox(view);
                            var up = view.UpDirection;
                            var right = view.RightDirection;
                            double bottomAlongUp;
                            double centreAlongRight;
                            if (box != null)
                            {
                                var corners = Corners(box).ToList();
                                bottomAlongUp = corners.Min(c => c.DotProduct(up));
                                centreAlongRight = (corners.Min(c => c.DotProduct(right)) + corners.Max(c => c.DotProduct(right))) / 2;
                            }
                            else
                            {
                                bottomAlongUp = DrawingSupport.ToFeet(LabelLayout.EstimatedTagBottom(DrawingSupport.ToMm(head.DotProduct(up)), 2, textPlan.SizeMm, view.Scale));
                                centreAlongRight = head.DotProduct(right);
                            }
                            var topAlongUp = DrawingSupport.ToFeet(LabelLayout.NoteTopUnderTag(DrawingSupport.ToMm(bottomAlongUp), gapMm, view.Scale));
                            var anchor = head + up * (topAlongUp - head.DotProduct(up)) + right * (centreAlongRight - head.DotProduct(right));
                            var options = new TextNoteOptions(textType.Id)
                            {
                                HorizontalAlignment = HorizontalTextAlignment.Center,
                                VerticalAlignment = VerticalTextAlignment.Top
                            };
                            note = TextNote.Create(doc, view.Id, anchor, text, options);
                            if (note == null) throw new InvalidOperationException("Revit returned no text note.");
                        });
                        if (reason != null)
                        {
                            skipped.Add(new JObject { ["room_id"] = roomId, ["reason"] = reason });
                            continue;
                        }
                        labelled.Add(new JObject
                        {
                            ["room_id"] = roomId,
                            ["room_name"] = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                            ["tag_id"] = RevitCompat.GetId(tag.Id),
                            ["tag_reused"] = existing != null,
                            ["text_note_id"] = RevitCompat.GetId(note.Id),
                            ["text"] = text,
                            ["measured_mm"] = size == null ? null : new JObject
                            {
                                ["x"] = Math.Round(size[0], 1),
                                ["y"] = Math.Round(size[1], 1)
                            }
                        });
                    }
                    var anyNew = labelled.Count > 0;
                    if (!anyNew)
                    {
                        group.RollBack();
                        return CommandResult.Fail("NOTHING_LABELLED: no room was labelled; see skipped. Nothing was created.", new JObject { ["skipped"] = skipped });
                    }
                    DrawingSupport.Finish(group, true);
                }
                catch (Exception ex)
                {
                    if (group.HasStarted() && !group.HasEnded()) group.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not label the rooms: " + ex.Message + " Nothing was created.");
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["view_name"] = view.Name,
                ["text_type"] = textPlan.ToJson(),
                ["tag_type_id"] = tagType == null ? (long?)null : RevitCompat.GetId(tagType.Id),
                ["labelled_count"] = labelled.Count,
                ["rooms"] = labelled,
                ["skipped"] = skipped,
                ["grouped"] = false
            });
        }

        /// <summary>The room's bounding rectangle along model X and Y in mm (finish boundary), or null.</summary>
        private static double[] MeasuredSize(Room room)
        {
            var loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish
            });
            if (loops == null) return null;
            double xMin = double.MaxValue, yMin = double.MaxValue, xMax = double.MinValue, yMax = double.MinValue;
            foreach (var loop in loops)
                foreach (var segment in loop)
                {
                    var curve = segment.GetCurve();
                    if (curve == null) continue;
                    foreach (var pt in curve.Tessellate())
                    {
                        xMin = Math.Min(xMin, pt.X);
                        yMin = Math.Min(yMin, pt.Y);
                        xMax = Math.Max(xMax, pt.X);
                        yMax = Math.Max(yMax, pt.Y);
                    }
                }
            if (xMin > xMax || yMin > yMax) return null;
            return new[] { DrawingSupport.ToMm(xMax - xMin), DrawingSupport.ToMm(yMax - yMin) };
        }

        private static IEnumerable<XYZ> Corners(BoundingBoxXYZ box)
        {
            var t = box.Transform ?? Transform.Identity;
            foreach (var x in new[] { box.Min.X, box.Max.X })
                foreach (var y in new[] { box.Min.Y, box.Max.Y })
                    foreach (var z in new[] { box.Min.Z, box.Max.Z })
                        yield return t.OfPoint(new XYZ(x, y, z));
        }
    }
}
