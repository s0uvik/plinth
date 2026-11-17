// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Annotations/TagAllWallsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: optional view_id (default still the active view) and tag_type_id; walls already tagged in
// the view are skipped (rvt-mcp tagged them again on every call); per-wall failures are reported instead of
// swallowed; when no wall could be tagged the transaction is rolled back and the command fails; view templates
// and views that cannot hold tags are refused.

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

    public sealed class TagAllWallsHandler : IRevitCommand
    {
        public string Name => "tag_all_walls";
        public string Description =>
            "Tag every untagged wall visible in a view (view_id, default the active view) at the middle of its location line. "
            + "Optional tag_type_id; default is the project's default wall tag.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""view_id"":{""type"":""integer""},
            ""tag_type_id"":{""type"":""integer""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            if (!AnnotationHelpers.TryAnnotationView(doc, p.Value<long?>("view_id"), out View view, out var failure)) return failure;
            if (!AnnotationHelpers.TryTagType(doc, p.Value<long?>("tag_type_id"), out var tagType, out failure)) return failure;
            if (tagType != null && tagType.Category?.BuiltInCategory != BuiltInCategory.OST_WallTags)
                return CommandResult.Fail("TYPE_NOT_FOUND: tag_type_id " + RevitCompat.GetId(tagType.Id) + " is not a wall tag type.");
            if (tagType == null && new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_WallTags).FirstElement() == null)
                return CommandResult.Fail("NO_WALL_TAG_TYPE: no wall tag family is loaded in the project.");

            var walls = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType().OfType<Wall>().ToList();
            var viewIdValue = RevitCompat.GetId(view.Id);
            if (walls.Count == 0)
                return CommandResult.Ok(new { view_id = viewIdValue, tagged = 0, totalWalls = 0, message = "No walls are visible in this view." });

            var already = AnnotationHelpers.TaggedInView(doc, view);
            var untagged = walls.Where(w => !already.Contains(RevitCompat.GetId(w.Id))).ToList();
            if (untagged.Count == 0)
                return CommandResult.Ok(new { view_id = viewIdValue, tagged = 0, totalWalls = walls.Count, message = "Every wall in this view is already tagged." });

            var tagIds = new List<long>();
            var failed = new List<object>();
            using (var tx = new Transaction(doc, "Plinth: Tag All Walls"))
            {
                tx.Start();
                try
                {
                    if (tagType != null && !tagType.IsActive) { tagType.Activate(); doc.Regenerate(); }
                    foreach (var wall in untagged)
                    {
                        try
                        {
                            var curve = (wall.Location as LocationCurve)?.Curve;
                            if (curve == null) throw new InvalidOperationException("the wall has no location line.");
                            var tag = AnnotationHelpers.CreateTag(doc, view, wall, tagType, false, TagOrientation.Horizontal, curve.Evaluate(0.5, true));
                            tagIds.Add(RevitCompat.GetId(tag.Id));
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new { wallId = RevitCompat.GetId(wall.Id), error = ex.Message });
                        }
                    }
                    if (tagIds.Count == 0)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("TAG_FAILED: no wall could be tagged; see failed. Nothing was created.",
                            new { view_id = viewIdValue, tagged = 0, totalWalls = walls.Count, failed });
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("TAG_FAILED: could not tag the walls: " + ex.Message + " Nothing was created.");
                }
            }

            return CommandResult.Ok(new
            {
                view_id = viewIdValue,
                view_name = view.Name,
                tagged = tagIds.Count,
                totalWalls = walls.Count,
                alreadyTagged = walls.Count - untagged.Count,
                tagIds,
                failed
            });
        }
    }
}
