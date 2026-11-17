// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Rooms/ListAreasHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: unknown level or area scheme names are errors listing the valid ones (rvt-mcp
// returned an empty list); the status counts cover the filtered areas, as list_rooms does; mm via Mm.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Rooms
{
    public sealed class ListAreasHandler : IRevitCommand
    {
        public string Name => "list_areas";
        public string Description => "List areas with status (placed, not_enclosed), area in m2, level, area scheme and area plan. Filter by area scheme, level, status.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""area_scheme_name"":{""type"":""string""},
            ""level_name"":{""type"":""string""},
            ""status"":{""type"":""string"",""enum"":[""all"",""placed"",""not_enclosed""]},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var schemeName = p.Value<string>("area_scheme_name");
            var levelName = p.Value<string>("level_name");
            var statusFilter = p.Value<string>("status") ?? "all";
            var limit = p.Value<int?>("limit") ?? 5000;
            if (limit < 1 || limit > 20000) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to 20000.");

            CommandResult failure;
            AreaScheme scheme = null;
            if (!string.IsNullOrEmpty(schemeName) && !RoomsHelpers.TryAreaScheme(doc, schemeName, out scheme, out failure)) return failure;
            Level levelFilter = null;
            if (!string.IsNullOrEmpty(levelName) && !Lookup.TryLevel(doc, levelName, out levelFilter, out failure)) return failure;

            var areas = new JArray();
            int placed = 0, notEnclosed = 0, matching = 0;
            foreach (var a in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Areas).WhereElementIsNotElementType().OfType<Area>())
            {
                var plan = a.OwnerViewId != ElementId.InvalidElementId ? doc.GetElement(a.OwnerViewId) as ViewPlan : null;
                var areaScheme = a.AreaScheme ?? plan?.AreaScheme;
                var level = a.Level ?? plan?.GenLevel;
                if (scheme != null && (areaScheme == null || areaScheme.Id != scheme.Id)) continue;
                if (levelFilter != null && (level == null || level.Id != levelFilter.Id)) continue;

                var status = a.Area > 0.0001 && a.Location != null ? "placed" : "not_enclosed";
                if (status == "placed") placed++;
                else notEnclosed++;
                if (statusFilter != "all" && status != statusFilter) continue;
                matching++;
                if (areas.Count >= limit) continue;

                areas.Add(new JObject
                {
                    ["element_id"] = RevitCompat.GetId(a.Id),
                    ["unique_id"] = a.UniqueId,
                    ["name"] = a.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? a.Name,
                    ["number"] = a.Number,
                    ["status"] = status,
                    ["area_m2"] = RoomsHelpers.M2(a.Area),
                    ["level"] = RoomsHelpers.Ref(level),
                    ["area_scheme"] = RoomsHelpers.Ref(areaScheme),
                    ["area_plan"] = RoomsHelpers.Ref(plan),
                    ["location"] = RoomsHelpers.Point((a.Location as LocationPoint)?.Point)
                });
            }

            return CommandResult.Ok(new JObject
            {
                ["total"] = matching,
                ["returned"] = areas.Count,
                ["truncated"] = matching > areas.Count,
                ["counts"] = new JObject { ["placed"] = placed, ["not_enclosed"] = notEnclosed },
                ["areas"] = areas
            });
        }
    }
}
