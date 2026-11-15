// Written for B2M (no rvt-mcp source).

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.View
{
    // B2M.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class ListViewsHandler : IRevitCommand
    {
        private static readonly ViewType[] Hidden =
            { ViewType.Undefined, ViewType.Internal, ViewType.ProjectBrowser, ViewType.SystemBrowser };

        public string Name => "list_views";
        public string Description =>
            "List views and sheets: id, name, view type, level, template flag; for sheets their number, for views the sheets they are placed on. "
            + "Optional viewType filter by Revit ViewType name (FloorPlan, CeilingPlan, ThreeD, Section, Elevation, DrawingSheet, Schedule, Legend, ...).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""viewType"":{""type"":""string""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var filterName = p.Value<string>("viewType");
            ViewType? filter = null;
            if (!string.IsNullOrEmpty(filterName))
            {
                if (!Enum.TryParse(filterName, true, out ViewType parsed) || !Enum.IsDefined(typeof(ViewType), parsed) || Hidden.Contains(parsed))
                    return CommandResult.Fail("INVALID_PARAMS: '" + filterName + "' is not a view type.", new JObject
                    {
                        ["valid_view_types"] = new JArray(Enum.GetNames(typeof(ViewType))
                            .Where(n => !Hidden.Contains((ViewType)Enum.Parse(typeof(ViewType), n))))
                    });
                filter = parsed;
            }

            // Where each view sits: viewports for ordinary views, schedule instances for schedules.
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .ToDictionary(s => RevitCompat.GetId(s.Id));
            var placements = new Dictionary<long, List<ViewSheet>>();
            void Place(ElementId viewId, ElementId sheetId)
            {
                if (!sheets.TryGetValue(RevitCompat.GetId(sheetId), out var sheet)) return;
                var key = RevitCompat.GetId(viewId);
                if (!placements.TryGetValue(key, out var list)) placements[key] = list = new List<ViewSheet>();
                if (!list.Contains(sheet)) list.Add(sheet);
            }
            foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
                Place(vp.ViewId, vp.SheetId);
            foreach (var si in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
                if (!si.IsTitleblockRevisionSchedule) Place(si.ScheduleId, si.OwnerViewId);

            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !Hidden.Contains(v.ViewType))
                .Where(v => !(v is ViewSchedule vs && vs.IsTitleblockRevisionSchedule))
                .Where(v => filter == null || v.ViewType == filter.Value)
                .OrderBy(v => v.ViewType.ToString()).ThenBy(v => v is ViewSheet s ? s.SheetNumber : v.Name)
                .ToList();

            var rows = new JArray();
            foreach (var v in views)
            {
                var row = new JObject
                {
                    ["id"] = RevitCompat.GetId(v.Id),
                    ["name"] = v.Name,
                    ["viewType"] = v.ViewType.ToString(),
                    ["levelName"] = v.GenLevel?.Name,
                    ["isTemplate"] = v.IsTemplate
                };
                if (v is ViewSheet sheet)
                {
                    row["sheetNumber"] = sheet.SheetNumber;
                    row["isPlaceholder"] = sheet.IsPlaceholder;
                }
                else
                {
                    placements.TryGetValue(RevitCompat.GetId(v.Id), out var on);
                    row["sheets"] = new JArray((on ?? new List<ViewSheet>()).OrderBy(s => s.SheetNumber).Select(s => new JObject
                    {
                        ["id"] = RevitCompat.GetId(s.Id),
                        ["number"] = s.SheetNumber,
                        ["name"] = s.Name
                    }));
                }
                rows.Add(row);
            }

            return CommandResult.Ok(new JObject { ["count"] = rows.Count, ["views"] = rows });
        }
    }
}
