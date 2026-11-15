// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Workflows/WorkflowRoomDocumentationHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: the finish schedule is made by create_schedule and placed by place_schedule_on_sheet
// (called in-process; fields by BuiltInParameter name, so not language-dependent); view_id (new) picks the parent
// view for callouts and tags (default the active view); the callout uses the parent view's own type (rvt-mcp took
// the first detail, section or plan type, which may not fit the parent); room_ids that are not rooms, an unknown
// level_name, or a bad sheet_id are errors with the valid options (rvt-mcp warned and skipped); callout viewports
// go on a grid inside the title block frame (rvt-mcp used fixed foot offsets); padding goes through Mm;
// transactions are "B2M: ..."; any Revit warning rolls the whole group back (FailureScope).

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using B2M.Revit.Commands.Schedule;
using B2M.Revit.Commands.Sheets;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Workflows
{
    using View = Autodesk.Revit.DB.View;

    public sealed class WorkflowRoomDocumentationHandler : IRevitCommand
    {
        private const double CalloutPaddingMm = 1000;

        public string Name => "workflow_room_documentation";
        public string Description =>
            "Document placed rooms (room_ids, or all on level_name; limit default 50, at most 200): per-room callouts of the parent view "
            + "(view_id, default the active plan), room tags, a room finish schedule, and optional placement on sheet_id. dry_run (default true) only reports.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""room_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""level_name"":{""type"":""string""},
            ""view_id"":{""type"":""integer""},
            ""create_callouts"":{""type"":""boolean""},
            ""create_finish_schedule"":{""type"":""boolean""},
            ""tag_rooms"":{""type"":""boolean""},
            ""sheet_id"":{""type"":""integer""},
            ""dry_run"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var roomIds = p["room_ids"]?.ToObject<long[]>();
            var levelName = p.Value<string>("level_name");
            var createCallouts = p.Value<bool?>("create_callouts") ?? true;
            var createSchedule = p.Value<bool?>("create_finish_schedule") ?? true;
            var tagRooms = p.Value<bool?>("tag_rooms") ?? true;
            var dryRun = p.Value<bool?>("dry_run") ?? true;
            var limit = p.Value<int?>("limit") ?? 50;
            if (limit < 1 || limit > 200) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to 200.");
            if (roomIds != null && roomIds.Length > 0 && !string.IsNullOrEmpty(levelName))
                return CommandResult.Fail("INVALID_PARAMS: pass room_ids or level_name, not both.");

            CommandResult failure;
            ViewSheet sheet = null;
            var sheetId = p.Value<long?>("sheet_id");
            if (sheetId.HasValue && !Lookup.TrySheet(doc, sheetId, null, out sheet, out failure)) return failure;

            View parentView;
            var viewId = p.Value<long?>("view_id");
            if (viewId.HasValue)
            {
                if (!Lookup.TryView(doc, viewId.Value, out parentView, out failure)) return failure;
            }
            else parentView = doc.ActiveView;
            if ((createCallouts || tagRooms) && !IsPlan(parentView))
                return CommandResult.Fail("VIEW_NOT_PLAN: callouts and room tags need a floor or ceiling plan; '" + (parentView?.Name ?? "?")
                    + "' is a " + (parentView?.ViewType.ToString() ?? "missing view") + ". Pass view_id of a plan, or turn create_callouts and tag_rooms off.");

            var warnings = new List<string>();
            List<Room> rooms;
            bool truncated;
            if (roomIds != null && roomIds.Length > 0)
            {
                var notRooms = roomIds.Where(id => !(doc.GetElement(RevitCompat.ToElementId(id)) is Room)).ToList();
                if (notRooms.Count > 0)
                    return CommandResult.Fail("ELEMENT_NOT_FOUND: these ids are not rooms: " + string.Join(", ", notRooms) + ". list_rooms lists the rooms.");
                rooms = new List<Room>();
                foreach (var id in roomIds.Distinct())
                {
                    var room = (Room)doc.GetElement(RevitCompat.ToElementId(id));
                    var roomStatus = RoomStatus.Of(room);
                    if (roomStatus != "placed") warnings.Add("Room " + id + " is " + roomStatus + " and was skipped.");
                    else rooms.Add(room);
                }
                truncated = rooms.Count > limit;
                rooms = rooms.Take(limit).ToList();
            }
            else
            {
                Level level = null;
                if (!string.IsNullOrEmpty(levelName) && !Lookup.TryLevel(doc, levelName, out level, out failure)) return failure;
                rooms = new List<Room>();
                truncated = false;
                foreach (var room in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>())
                {
                    if (level != null && room.LevelId != level.Id) continue;
                    if (RoomStatus.Of(room) != "placed") continue;
                    if (rooms.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }
                    rooms.Add(room);
                }
            }
            if (truncated) warnings.Add("The room list was cut at limit " + limit + ".");

            var steps = new JArray();
            var createdIds = new List<long>();
            var roomDocs = new JArray(rooms.Select(Preview));
            steps.Add(WorkflowsHelpers.Step("Collect Rooms", "list_rooms (local)", "succeeded", "Resolve placed rooms by room_ids or level_name.",
                new { requested = roomIds?.Length ?? 0, returned = rooms.Count, truncated }));
            if (rooms.Count == 0)
            {
                warnings.Add("No placed rooms matched; nothing to document.");
                return CommandResult.Ok(Result(dryRun, "succeeded", steps, createdIds, warnings, roomDocs, null,
                    WorkflowsHelpers.Rollback("None", false, "No rooms.")));
            }

            if (dryRun)
            {
                if (createSchedule)
                    steps.Add(WorkflowsHelpers.Step("Create Finish Schedule", "create_schedule", "skipped", "Dry run: one room finish schedule would be created.", new { planned = true }));
                if (createCallouts)
                    steps.Add(WorkflowsHelpers.Step("Create Room Callouts", "ViewSection.CreateCallout", "skipped",
                        "Dry run: one callout per room would be created in '" + parentView.Name + "'.", new { rooms = rooms.Count }));
                if (tagRooms)
                    steps.Add(WorkflowsHelpers.Step("Tag Rooms", "NewRoomTag", "skipped", "Dry run: rooms would be tagged.", new { rooms = rooms.Count }));
                if (sheet != null)
                    steps.Add(WorkflowsHelpers.Step("Place On Sheet", "Viewport.Create", "skipped", "Dry run: callouts and schedule would go on sheet " + sheet.SheetNumber + ".", null));
                return CommandResult.Ok(Result(true, "succeeded", steps, createdIds, warnings, roomDocs, null,
                    WorkflowsHelpers.Rollback("TransactionGroup", false, "Dry run; nothing was written.")));
            }

            var status = "succeeded";
            long? scheduleId = null;
            using (var group = new TransactionGroup(doc, "B2M: Workflow room documentation"))
            {
                group.Start();

                if (createSchedule)
                {
                    var name = WorkflowsHelpers.UniqueName(
                        new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Select(s => s.Name), "B2M Room Finish Schedule");
                    var result = WorkflowsHelpers.Invoke(new CreateScheduleHandler(), app, new JObject
                    {
                        ["category"] = "OST_Rooms",
                        ["name"] = name,
                        ["fields"] = new JArray("ROOM_NUMBER", "ROOM_NAME", "ROOM_LEVEL_ID", "ROOM_FINISH_BASE", "ROOM_FINISH_FLOOR", "ROOM_FINISH_CEILING", "ROOM_FINISH_WALL")
                    });
                    if (result.Success)
                    {
                        scheduleId = result.Data.Value<long>("scheduleId");
                        createdIds.Add(scheduleId.Value);
                        steps.Add(WorkflowsHelpers.Step("Create Finish Schedule", "create_schedule", "succeeded", "Create a room finish schedule.", result.Data));
                    }
                    else
                    {
                        status = "partial";
                        warnings.Add("Finish schedule: " + result.Error);
                        steps.Add(WorkflowsHelpers.Step("Create Finish Schedule", "create_schedule", "failed", "Create a room finish schedule.", result.Data, result.Error));
                    }
                }

                var updated = new JArray();
                using (var tx = new Transaction(doc, "B2M: Room documentation"))
                {
                    tx.Start();
                    try
                    {
                        var calloutViews = new List<(View view, JObject report)>();
                        foreach (var room in rooms)
                        {
                            var report = Preview(room);
                            using (var sub = new SubTransaction(doc))
                            {
                                sub.Start();
                                var before = createdIds.Count;
                                try
                                {
                                    View callout = null;
                                    if (createCallouts)
                                    {
                                        callout = CreateCallout(doc, parentView, room, report);
                                        if (callout != null) createdIds.Add(RevitCompat.GetId(callout.Id));
                                    }
                                    if (tagRooms)
                                    {
                                        var tag = TagRoom(doc, room, callout ?? parentView, report);
                                        if (tag != null) createdIds.Add(RevitCompat.GetId(tag.Id));
                                    }
                                    if (sub.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("the room step did not commit.");
                                    if (callout != null) calloutViews.Add((callout, report));
                                }
                                catch (Exception ex)
                                {
                                    status = "partial";
                                    if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                                    createdIds.RemoveRange(before, createdIds.Count - before);
                                    report["callout_view_id"] = JValue.CreateNull();
                                    report["tag_ids"] = new JArray();
                                    report["status"] = "failed";
                                    ((JArray)report["warnings"]).Add(ex.Message);
                                    warnings.Add("Room " + RevitCompat.GetId(room.Id) + ": " + ex.Message);
                                }
                            }
                            updated.Add(report);
                        }

                        if (sheet != null && calloutViews.Count > 0)
                        {
                            var cells = WorkflowsHelpers.GridCells(doc, sheet, calloutViews.Count + (scheduleId.HasValue ? 1 : 0), out var usedFrame);
                            if (!usedFrame) warnings.Add("Sheet " + sheet.SheetNumber + " has no title block; views were laid out on an A1 area at the sheet origin.");
                            for (var i = 0; i < calloutViews.Count; i++)
                            {
                                var (view, report) = calloutViews[i];
                                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                                {
                                    ((JArray)report["warnings"]).Add("the callout cannot be placed on sheet " + sheet.SheetNumber + ".");
                                    status = "partial";
                                    continue;
                                }
                                var viewport = Viewport.Create(doc, sheet.Id, view.Id, cells[i].center);
                                createdIds.Add(RevitCompat.GetId(viewport.Id));
                                report["viewport_id"] = RevitCompat.GetId(viewport.Id);
                            }
                        }

                        steps.Add(WorkflowsHelpers.Step("Document Rooms", "ViewSection.CreateCallout / NewRoomTag / Viewport.Create",
                            status == "succeeded" ? "succeeded" : "partial", "Per-room callouts, tags and sheet placement.",
                            new { rooms = updated.Count, created = createdIds.Count }));

                        if (tx.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Revit rolled the room documentation transaction back; see failures.");
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                        group.RollBack();
                        steps.Add(WorkflowsHelpers.Step("Commit Room Documentation", "Transaction", "failed", "Commit the room documentation.", null, ex.Message));
                        return CommandResult.Ok(Result(false, "failed", steps, new long[0], warnings, roomDocs, null,
                            WorkflowsHelpers.Rollback("TransactionGroup", true, "The room documentation failed and was rolled back; nothing was changed.")));
                    }
                }
                roomDocs = updated;

                if (sheet != null && scheduleId.HasValue)
                {
                    var cells = WorkflowsHelpers.GridCells(doc, sheet, roomDocs.Count(r => r["viewport_id"] != null) + 1, out _);
                    var at = cells[cells.Count - 1].topLeft;
                    var placed = WorkflowsHelpers.Invoke(new PlaceScheduleOnSheetHandler(), app, new JObject
                    {
                        ["sheet_id"] = RevitCompat.GetId(sheet.Id),
                        ["schedule_id"] = scheduleId.Value,
                        ["x_mm"] = Mm.FromFeet(at.X),
                        ["y_mm"] = Mm.FromFeet(at.Y)
                    });
                    if (placed.Success)
                    {
                        createdIds.Add(placed.Data.Value<long>("schedule_instance_id"));
                        steps.Add(WorkflowsHelpers.Step("Place Finish Schedule On Sheet", "place_schedule_on_sheet", "succeeded", "Place the finish schedule on the sheet.", placed.Data));
                    }
                    else
                    {
                        status = "partial";
                        warnings.Add("Finish schedule placement: " + placed.Error);
                        steps.Add(WorkflowsHelpers.Step("Place Finish Schedule On Sheet", "place_schedule_on_sheet", "failed", "Place the finish schedule on the sheet.", placed.Data, placed.Error));
                    }
                }

                if (group.Assimilate() != TransactionStatus.Committed)
                    return CommandResult.Fail("NOT_COMMITTED: Revit did not commit the room documentation; nothing was changed.");
            }

            return CommandResult.Ok(Result(false, status, steps, createdIds, warnings, roomDocs, scheduleId,
                WorkflowsHelpers.Rollback("TransactionGroup", false, "Room documentation committed.")));
        }

        private JObject Result(bool dryRun, string status, JArray steps, IEnumerable<long> created, IEnumerable<string> warnings,
            JArray roomDocs, long? scheduleId, JObject rollback)
        {
            var result = WorkflowsHelpers.Envelope(Name, dryRun, status, steps, created, new long[0], warnings, rollback);
            result["finish_schedule_id"] = scheduleId.HasValue ? new JValue(scheduleId.Value) : JValue.CreateNull();
            result["room_documentation"] = roomDocs ?? new JArray();
            return result;
        }

        private static bool IsPlan(View view) =>
            view != null && !view.IsTemplate && (view.ViewType == ViewType.FloorPlan || view.ViewType == ViewType.CeilingPlan
                || view.ViewType == ViewType.EngineeringPlan || view.ViewType == ViewType.AreaPlan);

        private static JObject Preview(Room room) => new JObject
        {
            ["room_id"] = RevitCompat.GetId(room.Id),
            ["room_number"] = room.Number,
            ["room_name"] = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
            ["level_name"] = room.Level?.Name ?? "",
            ["status"] = "planned",
            ["callout_view_id"] = JValue.CreateNull(),
            ["tag_ids"] = new JArray(),
            ["warnings"] = new JArray()
        };

        private static View CreateCallout(Document doc, View parent, Room room, JObject report)
        {
            var box = room.get_BoundingBox(parent) ?? room.get_BoundingBox(null);
            if (box == null)
            {
                ((JArray)report["warnings"]).Add("the room has no bounding box; no callout.");
                return null;
            }
            var pad = Mm.ToFeet(CalloutPaddingMm);
            var min = new XYZ(box.Min.X - pad, box.Min.Y - pad, box.Min.Z);
            var max = new XYZ(box.Max.X + pad, box.Max.Y + pad, box.Max.Z);
            var callout = ViewSection.CreateCallout(doc, parent.Id, parent.GetTypeId(), min, max);
            callout.Name = WorkflowsHelpers.UniqueName(WorkflowsHelpers.ViewNames(doc),
                WorkflowsHelpers.NormalizeName("Room " + room.Number + " - " + room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()));
            report["callout_view_id"] = RevitCompat.GetId(callout.Id);
            report["status"] = "created";
            return callout;
        }

        private static RoomTag TagRoom(Document doc, Room room, View view, JObject report)
        {
            var point = ((LocationPoint)room.Location).Point;
            var tag = doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(point.X, point.Y), view.Id);
            if (tag == null)
            {
                ((JArray)report["warnings"]).Add("Revit did not create a room tag in '" + view.Name + "'.");
                return null;
            }
            ((JArray)report["tag_ids"]).Add(RevitCompat.GetId(tag.Id));
            report["status"] = "created";
            return tag;
        }
    }
}
