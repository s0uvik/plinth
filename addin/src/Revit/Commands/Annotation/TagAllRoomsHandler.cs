// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Rooms/TagAllRoomsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: view_id picks the plan (default still the active view); the tag type is chosen by
// tag_type_id or tag_type_name (+ tag_family_name), else the project's default room tag type (rvt-mcp
// used the first room tag type found); view templates are refused; per-room failures are reported and
// the tags that worked are kept, as before.

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

    public sealed class TagAllRoomsHandler : IRevitCommand
    {
        public string Name => "tag_all_rooms";
        public string Description =>
            "Tag every untagged placed room in a plan view (view_id, default the active view) at the room's location point. "
            + "Optional tag type by tag_type_id or tag_type_name (+ tag_family_name); default is the project's default room tag.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""view_id"":{""type"":""integer""},
            ""tag_type_id"":{""type"":""integer""},
            ""tag_type_name"":{""type"":""string""},
            ""tag_family_name"":{""type"":""string""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            View view;
            CommandResult failure;
            var viewId = p.Value<long?>("view_id");
            if (viewId.HasValue)
            {
                if (!Lookup.TryView(doc, viewId.Value, out view, out failure)) return failure;
            }
            else
            {
                view = doc.ActiveView;
                if (view == null) return CommandResult.Fail("NO_ACTIVE_VIEW: the document has no active view; pass view_id.");
            }
            if (!(view is ViewPlan))
                return CommandResult.Fail("NOT_A_PLAN_VIEW: room tags need a plan view; '" + view.Name + "' is a " + view.ViewType + ".");

            FamilySymbol tagType = null;
            var typeId = p.Value<long?>("tag_type_id");
            var typeName = p.Value<string>("tag_type_name");
            var familyName = p.Value<string>("tag_family_name");
            if (!string.IsNullOrEmpty(familyName) && string.IsNullOrEmpty(typeName))
                return CommandResult.Fail("INVALID_PARAMS: tag_family_name needs tag_type_name.");
            if (typeId.HasValue || !string.IsNullOrEmpty(typeName))
            {
                if (!Lookup.TryFamilySymbol(doc, typeId, familyName, typeName, BuiltInCategory.OST_RoomTags, out tagType, out failure))
                    return failure;
                if (tagType.Category?.BuiltInCategory != BuiltInCategory.OST_RoomTags)
                    return CommandResult.Fail("TYPE_NOT_FOUND: type " + typeId + " is not a room tag type.");
            }
            else if (new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_RoomTags).FirstElement() == null)
            {
                return CommandResult.Fail("NO_ROOM_TAG_TYPE: no room tag family is loaded in the project.");
            }

            var rooms = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .Cast<Room>().Where(r => r.Area > 0).ToList();
            if (rooms.Count == 0)
                return CommandResult.Ok(new { view_id = RevitCompat.GetId(view.Id), tagged = 0, totalRooms = 0, message = "No placed rooms in this view." });

            var alreadyTagged = new HashSet<long>(new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_RoomTags)
                .WhereElementIsNotElementType().Cast<RoomTag>()
                .Where(t => !t.IsTaggingLink && t.Room != null)
                .Select(t => RevitCompat.GetId(t.Room.Id)));
            var untagged = rooms.Where(r => !alreadyTagged.Contains(RevitCompat.GetId(r.Id))).ToList();
            if (untagged.Count == 0)
                return CommandResult.Ok(new { view_id = RevitCompat.GetId(view.Id), tagged = 0, totalRooms = rooms.Count, message = "Every room in this view is already tagged." });

            var tagIds = new List<long>();
            var failed = new List<object>();
            using (var tx = new Transaction(doc, "B2M: Tag All Rooms"))
            {
                tx.Start();
                try
                {
                    foreach (var room in untagged)
                    {
                        try
                        {
                            var point = (room.Location as LocationPoint)?.Point;
                            if (point == null) throw new InvalidOperationException("the room has no location point.");
                            var tag = doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(point.X, point.Y), view.Id);
                            if (tag == null) throw new InvalidOperationException("Revit returned no tag.");
                            if (tagType != null && tag.GetTypeId() != tagType.Id) tag.ChangeTypeId(tagType.Id);
                            tagIds.Add(RevitCompat.GetId(tag.Id));
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new { roomId = RevitCompat.GetId(room.Id), error = ex.Message });
                        }
                    }
                    if (tagIds.Count == 0)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("TAG_FAILED: no room could be tagged; see failed. Nothing was created.",
                            new { view_id = RevitCompat.GetId(view.Id), tagged = 0, totalRooms = rooms.Count, failed });
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("TAG_FAILED: could not tag the rooms: " + ex.Message + " Nothing was created.");
                }
            }

            return CommandResult.Ok(new
            {
                view_id = RevitCompat.GetId(view.Id),
                view_name = view.Name,
                tagged = tagIds.Count,
                totalRooms = rooms.Count,
                alreadyTagged = rooms.Count - untagged.Count,
                tagTypeId = tagType == null ? (long?)null : RevitCompat.GetId(tagType.Id),
                tagIds,
                failed
            });
        }
    }
}
