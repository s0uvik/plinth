// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Annotations/TagElementsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: with tag_type_id the tag is placed with that type directly (rvt-mcp placed the category
// default and then changed its type, which failed when no default tag was loaded); an element with no location
// or bounding box is a per-element failure (rvt-mcp tagged it at the project origin); when nothing could be tagged
// the transaction is rolled back; offsets go through Mm; the view is resolved by Lookup.TryView.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Annotation
{
    // Plinth.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class TagElementsHandler : IRevitCommand
    {
        public string Name => "tag_elements";
        public string Description =>
            "Tag elements visible in a view (view_id, default the active view). The tag goes at the element's location point, "
            + "the middle of its location line, or its bounding box centre, moved by offset_x/offset_y mm along the view's right/up. "
            + "Rooms get room tags (plan views only). Optional tag_type_id, orientation, leader.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""view_id"":{""type"":""integer""},
            ""tag_type_id"":{""type"":""integer""},
            ""orientation"":{""type"":""string"",""enum"":[""Horizontal"",""Vertical""]},
            ""leader"":{""type"":""boolean""},
            ""offset_x"":{""type"":""number""},
            ""offset_y"":{""type"":""number""}},
            ""required"":[""element_ids""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var elementIds = ((JArray)p["element_ids"]).Select(t => t.Value<long>()).Distinct().ToList();
            if (elementIds.Count == 0) return CommandResult.Fail("INVALID_PARAMS: element_ids must not be empty.");
            if (!AnnotationHelpers.TryAnnotationView(doc, p.Value<long?>("view_id"), out View view, out var failure)) return failure;
            if (!AnnotationHelpers.TryTagType(doc, p.Value<long?>("tag_type_id"), out var tagType, out failure)) return failure;

            var orientation = p.Value<string>("orientation") == "Vertical" ? TagOrientation.Vertical : TagOrientation.Horizontal;
            var leader = p.Value<bool?>("leader") ?? false;
            var offset = view.RightDirection * Mm.ToFeet(p.Value<double?>("offset_x") ?? 0)
                + view.UpDirection * Mm.ToFeet(p.Value<double?>("offset_y") ?? 0);

            var visible = new HashSet<long>(new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType()
                .ToElementIds().Select(RevitCompat.GetId));

            var tags = new JArray();
            var failed = new JArray();
            using (var tx = new Transaction(doc, "Plinth: Tag Elements"))
            {
                tx.Start();
                try
                {
                    if (tagType != null && !tagType.IsActive) { tagType.Activate(); doc.Regenerate(); }
                    foreach (var id in elementIds)
                    {
                        var element = doc.GetElement(RevitCompat.ToElementId(id));
                        string error = null;
                        if (element == null) error = "element not found.";
                        else if (!visible.Contains(id)) error = "element is not visible in the view.";
                        if (error == null)
                        {
                            try
                            {
                                var anchor = AnnotationHelpers.Anchor(element, view);
                                if (anchor == null) throw new InvalidOperationException("the element has no location or bounding box to tag at.");
                                var tag = AnnotationHelpers.CreateTag(doc, view, element, tagType, leader, orientation, anchor + offset);
                                var head = AnnotationHelpers.HeadPosition(tag);
                                tags.Add(new JObject
                                {
                                    ["tag_id"] = RevitCompat.GetId(tag.Id),
                                    ["element_id"] = id,
                                    ["tag_type_id"] = RevitCompat.GetId(tag.GetTypeId()),
                                    ["tag_type_name"] = doc.GetElement(tag.GetTypeId())?.Name,
                                    ["head_position_mm"] = head == null ? null : AnnotationHelpers.PointMm(head)
                                });
                                continue;
                            }
                            catch (Exception ex)
                            {
                                error = ex.Message;
                            }
                        }
                        failed.Add(new JObject { ["element_id"] = id, ["error"] = error });
                    }

                    if (tags.Count == 0)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("TAG_FAILED: no element could be tagged; see failed. Nothing was created.",
                            new JObject { ["view_id"] = RevitCompat.GetId(view.Id), ["failed"] = failed });
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("TAG_FAILED: could not tag the elements: " + ex.Message + " Nothing was created.");
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["view_name"] = view.Name,
                ["requested"] = elementIds.Count,
                ["created"] = tags.Count,
                ["tags"] = tags,
                ["failed"] = failed
            });
        }
    }
}
