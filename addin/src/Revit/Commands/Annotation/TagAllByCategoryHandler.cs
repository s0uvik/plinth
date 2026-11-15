// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Annotations/TagAllByCategoryHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: a category is matched exactly by name or built-in name (rvt-mcp also took any OST_ name ending
// with the input) and a miss lists close matches; with tag_type_id the tag is placed with that type directly; an
// element with no location or bounding box is a per-element failure (rvt-mcp tagged it at the project origin);
// existing room, area and independent tags all count as "already tagged"; when nothing could be tagged the
// transaction is rolled back. ReadOnly is false: dry_run only reports, but the same command writes otherwise.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Annotation
{
    // B2M.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class TagAllByCategoryHandler : IRevitCommand
    {
        private static readonly double OffsetFeet = Mm.ToFeet(500);

        public string Name => "tag_all_by_category";
        public string Description =>
            "Tag every element of a category visible in a view (view_id, default the active view), 500 mm up and right of its "
            + "location. category is a name (Doors) or built-in name (OST_Doors). skip_existing (default true) leaves tagged "
            + "elements alone; dry_run lists what would be tagged; limit (default 200, max 500). Optional tag_type_id, leader.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""category"":{""type"":""string""},
            ""view_id"":{""type"":""integer""},
            ""tag_type_id"":{""type"":""integer""},
            ""skip_existing"":{""type"":""boolean""},
            ""leader"":{""type"":""boolean""},
            ""dry_run"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}},
            ""required"":[""category""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            if (!AnnotationHelpers.TryLimit(p, 200, 500, out var limit, out var failure)) return failure;
            if (!AnnotationHelpers.TryAnnotationView(doc, p.Value<long?>("view_id"), out View view, out failure)) return failure;
            if (!AnnotationHelpers.TryCategory(doc, p.Value<string>("category"), out var category, out failure)) return failure;
            if (!AnnotationHelpers.TryTagType(doc, p.Value<long?>("tag_type_id"), out var tagType, out failure)) return failure;
            var skipExisting = p.Value<bool?>("skip_existing") ?? true;
            var leader = p.Value<bool?>("leader") ?? false;
            var dryRun = p.Value<bool?>("dry_run") ?? false;

            var rooms = category.BuiltInCategory == BuiltInCategory.OST_Rooms;
            if (rooms && !(view is ViewPlan))
                return CommandResult.Fail("NOT_A_PLAN_VIEW: room tags need a plan view; '" + view.Name + "' is a " + view.ViewType + ".");
            if (rooms && tagType != null && tagType.Category?.BuiltInCategory != BuiltInCategory.OST_RoomTags)
                return CommandResult.Fail("TYPE_NOT_FOUND: tag_type_id " + RevitCompat.GetId(tagType.Id) + " is not a room tag type.");

            var elements = new FilteredElementCollector(doc, view.Id).OfCategoryId(category.Id).WhereElementIsNotElementType()
                .Where(e => !(e is Room room) || room.Area > 0).ToList();
            var tagged = AnnotationHelpers.TaggedInView(doc, view);
            var alreadyTagged = elements.Count(e => tagged.Contains(RevitCompat.GetId(e.Id)));
            var candidates = elements.Where(e => !skipExisting || !tagged.Contains(RevitCompat.GetId(e.Id))).ToList();
            var truncated = candidates.Count > limit;
            candidates = candidates.Take(limit).ToList();

            var result = new JObject
            {
                ["dry_run"] = dryRun,
                ["category"] = category.Name,
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["view_name"] = view.Name,
                ["visible_count"] = elements.Count,
                ["already_tagged"] = alreadyTagged,
                ["candidate_count"] = candidates.Count,
                ["truncated"] = truncated
            };
            if (dryRun)
            {
                result["would_create"] = candidates.Count;
                result["element_ids"] = new JArray(candidates.Select(e => RevitCompat.GetId(e.Id)));
                return CommandResult.Ok(result);
            }
            if (candidates.Count == 0)
            {
                result["created"] = 0;
                result["message"] = "Nothing to tag in this view.";
                return CommandResult.Ok(result);
            }

            var tags = new JArray();
            var failed = new JArray();
            using (var tx = new Transaction(doc, "B2M: Tag All By Category"))
            {
                tx.Start();
                try
                {
                    if (tagType != null && !tagType.IsActive) { tagType.Activate(); doc.Regenerate(); }
                    var offset = (view.RightDirection + view.UpDirection) * OffsetFeet;
                    foreach (var element in candidates)
                    {
                        var id = RevitCompat.GetId(element.Id);
                        try
                        {
                            var anchor = AnnotationHelpers.Anchor(element, view);
                            if (anchor == null) throw new InvalidOperationException("the element has no location or bounding box to tag at.");
                            var tag = AnnotationHelpers.CreateTag(doc, view, element, tagType, leader, TagOrientation.Horizontal, anchor + offset);
                            tags.Add(new JObject { ["tag_id"] = RevitCompat.GetId(tag.Id), ["element_id"] = id });
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new JObject { ["element_id"] = id, ["error"] = ex.Message });
                        }
                    }
                    if (tags.Count == 0)
                    {
                        tx.RollBack();
                        result["created"] = 0;
                        result["failed"] = failed;
                        return CommandResult.Fail("TAG_FAILED: no element could be tagged; see failed. Nothing was created.", result);
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

            result["created"] = tags.Count;
            result["tags"] = tags;
            result["failed"] = failed;
            return CommandResult.Ok(result);
        }
    }
}
