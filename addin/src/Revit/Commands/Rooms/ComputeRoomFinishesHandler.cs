// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Rooms/ComputeRoomFinishesHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: read-only (it only reads; rvt-mcp listed it as a write); the unused "output"
// option is dropped; an unknown level is an error listing the valid ones (rvt-mcp returned an empty
// result); room_ids and level_name are exclusive; boundary elements are merged per element with their
// total length in mm (rvt-mcp listed every segment) and capped at 100 per room; default limit 500
// rooms (max 5000) with truncation reported; finishes read from the built-in parameters only
// (rvt-mcp's English-name fallback is dropped).

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Rooms
{
    public sealed class ComputeRoomFinishesHandler : IRevitCommand
    {
        private const int MaxIds = 5000;
        private const int MaxBoundaryElements = 100;

        public string Name => "compute_room_finishes";
        public string Description =>
            "Read each room's base, floor, ceiling and wall finish parameters, the boundary elements with their materials and "
            + "length (mm), and a count of rooms per finish. Rooms by room_ids or level_name (default all).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""room_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""level_name"":{""type"":""string""},
            ""include_empty"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var roomIds = p["room_ids"]?.ToObject<long[]>();
            var levelName = p.Value<string>("level_name");
            var includeEmpty = p.Value<bool?>("include_empty") ?? true;
            var limit = p.Value<int?>("limit") ?? 500;
            if (limit < 1 || limit > 5000) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to 5000.");
            if (roomIds != null && !string.IsNullOrEmpty(levelName))
                return CommandResult.Fail("INVALID_PARAMS: pass room_ids or level_name, not both.");

            List<Room> rooms;
            if (roomIds != null)
            {
                if (roomIds.Length == 0 || roomIds.Length > MaxIds)
                    return CommandResult.Fail("INVALID_PARAMS: room_ids must hold 1 to " + MaxIds + " ids.");
                rooms = new List<Room>();
                var missing = new List<long>();
                var notRooms = new List<long>();
                foreach (var id in roomIds.Distinct())
                {
                    var element = doc.GetElement(RevitCompat.ToElementId(id));
                    if (element == null) missing.Add(id);
                    else if (element is Room room) rooms.Add(room);
                    else notRooms.Add(id);
                }
                if (missing.Count > 0 || notRooms.Count > 0)
                    return CommandResult.Fail("ELEMENT_NOT_FOUND: some room_ids are missing or are not rooms.",
                        new JObject { ["missing_ids"] = new JArray(missing.Take(100)), ["not_room_ids"] = new JArray(notRooms.Take(100)) });
            }
            else
            {
                Level level = null;
                if (!string.IsNullOrEmpty(levelName) && !Lookup.TryLevel(doc, levelName, out level, out var failure)) return failure;
                rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                    .OfType<Room>().Where(r => level == null || r.LevelId == level.Id).ToList();
            }

            var summary = new Dictionary<string, Dictionary<string, int>>
            {
                ["wall_finishes"] = new Dictionary<string, int>(),
                ["floor_finishes"] = new Dictionary<string, int>(),
                ["ceiling_finishes"] = new Dictionary<string, int>(),
                ["base_finishes"] = new Dictionary<string, int>()
            };
            var materialNames = new Dictionary<ElementId, string>();
            var results = new JArray();
            var matching = 0;
            foreach (var room in rooms)
            {
                var baseFinish = room.get_Parameter(BuiltInParameter.ROOM_FINISH_BASE)?.AsString() ?? "";
                var floorFinish = room.get_Parameter(BuiltInParameter.ROOM_FINISH_FLOOR)?.AsString() ?? "";
                var ceilingFinish = room.get_Parameter(BuiltInParameter.ROOM_FINISH_CEILING)?.AsString() ?? "";
                var wallFinish = room.get_Parameter(BuiltInParameter.ROOM_FINISH_WALL)?.AsString() ?? "";
                var empty = baseFinish == "" && floorFinish == "" && ceilingFinish == "" && wallFinish == "";
                if (empty && !includeEmpty) continue;
                matching++;
                Count(summary["wall_finishes"], wallFinish);
                Count(summary["floor_finishes"], floorFinish);
                Count(summary["ceiling_finishes"], ceilingFinish);
                Count(summary["base_finishes"], baseFinish);
                if (results.Count >= limit) continue;

                var warnings = new JArray();
                var boundary = new JArray();
                var boundaryTruncated = false;
                var status = "unplaced";
                if (room.Location is LocationPoint)
                {
                    IList<IList<BoundarySegment>> loops = null;
                    try
                    {
                        loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
                    }
                    catch (Exception ex)
                    {
                        warnings.Add("Boundary could not be read: " + ex.Message);
                    }
                    status = room.Area > 0.0001 && loops != null && loops.Count > 0 ? "placed" : "not_enclosed";
                    if (status == "placed")
                        boundaryTruncated = Boundary(doc, loops, materialNames, boundary, warnings);
                }

                results.Add(new JObject
                {
                    ["room"] = new JObject
                    {
                        ["element_id"] = RevitCompat.GetId(room.Id),
                        ["name"] = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "",
                        ["number"] = room.Number,
                        ["level_name"] = room.Level?.Name ?? ""
                    },
                    ["status"] = status,
                    ["finishes"] = new JObject
                    {
                        ["base_finish"] = baseFinish,
                        ["floor_finish"] = floorFinish,
                        ["ceiling_finish"] = ceilingFinish,
                        ["wall_finish"] = wallFinish
                    },
                    ["boundary_elements"] = boundary,
                    ["boundary_elements_truncated"] = boundaryTruncated,
                    ["warnings"] = warnings
                });
            }

            var summaryJson = new JObject();
            foreach (var kv in summary)
                summaryJson[kv.Key] = new JObject(kv.Value.OrderByDescending(e => e.Value).Take(200).Select(e => new JProperty(e.Key, e.Value)));
            return CommandResult.Ok(new JObject
            {
                ["total"] = matching,
                ["returned"] = results.Count,
                ["truncated"] = matching > results.Count,
                ["rooms"] = results,
                ["summary"] = summaryJson
            });
        }

        /// <summary>Boundary elements merged per element; returns true when capped.</summary>
        private static bool Boundary(Document doc, IList<IList<BoundarySegment>> loops, Dictionary<ElementId, string> materialNames,
            JArray output, JArray warnings)
        {
            var byElement = new Dictionary<(long host, long linked), JObject>();
            var truncated = false;
            foreach (var loop in loops)
            {
                foreach (var segment in loop)
                {
                    var id = segment.ElementId;
                    if (id == ElementId.InvalidElementId) continue;
                    var lengthFeet = segment.GetCurve()?.Length ?? 0;
                    var key = (RevitCompat.GetId(id), RevitCompat.GetId(segment.LinkElementId));
                    if (byElement.TryGetValue(key, out var existing))
                    {
                        existing["length_mm"] = Math.Round(existing.Value<double>("length_mm") + Mm.FromFeet(lengthFeet), 1);
                        continue;
                    }
                    if (byElement.Count >= MaxBoundaryElements)
                    {
                        truncated = true;
                        continue;
                    }
                    var inLink = segment.LinkElementId != ElementId.InvalidElementId;
                    // For a linked boundary, id is the link instance; its materials say nothing about the wall.
                    var element = inLink ? null : doc.GetElement(id);
                    var materials = new JArray();
                    if (element != null)
                    {
                        try
                        {
                            foreach (var matId in element.GetMaterialIds(false))
                            {
                                if (!materialNames.TryGetValue(matId, out var matName))
                                    materialNames[matId] = matName = (doc.GetElement(matId) as Material)?.Name;
                                if (matName != null) materials.Add(matName);
                            }
                        }
                        catch (Exception ex)
                        {
                            warnings.Add("Materials of element " + RevitCompat.GetId(id) + " could not be read: " + ex.Message);
                        }
                    }
                    var type = element == null ? null : doc.GetElement(element.GetTypeId()) as ElementType;
                    var row = new JObject
                    {
                        ["element_id"] = RevitCompat.GetId(id),
                        ["in_link"] = inLink,
                        ["linked_element_id"] = inLink ? new JValue(RevitCompat.GetId(segment.LinkElementId)) : null,
                        ["category"] = Lookup.BuiltInName(element?.Category) ?? element?.Category?.Name ?? "",
                        ["type_name"] = type?.Name ?? element?.Name ?? "",
                        ["material_names"] = materials,
                        ["length_mm"] = Mm.FromFeet(lengthFeet)
                    };
                    byElement[key] = row;
                    output.Add(row);
                }
            }
            return truncated;
        }

        private static void Count(Dictionary<string, int> summary, string finish)
        {
            var key = string.IsNullOrEmpty(finish) ? "Empty" : finish;
            summary[key] = summary.TryGetValue(key, out var n) ? n + 1 : 1;
        }
    }
}
