// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Schedules/ListSchedulesHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: each schedule also reports its BuiltInCategory name (language-independent, e.g. OST_Doors)
// and categoryFilter matches either name; the category name lookup per schedule replaces rvt-mcp's map over
// every BuiltInCategory; failures to read one schedule are counted in skipped, as before.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Schedule
{
    public sealed class ListSchedulesHandler : IRevitCommand
    {
        public string Name => "list_schedules";
        public string Description =>
            "List the project's schedules: id, name, category, key/itemized/template flags and field, filter and sort counts. "
            + "Optional categoryFilter (substring of the category name or its OST_ name) and namePattern (substring of the schedule name).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""categoryFilter"":{""type"":""string""},
            ""namePattern"":{""type"":""string""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var categoryFilter = p.Value<string>("categoryFilter");
            var namePattern = p.Value<string>("namePattern");

            var rows = new JArray();
            var skipped = 0;
            foreach (var schedule in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().OrderBy(s => s.Name))
            {
                try
                {
                    if (!string.IsNullOrEmpty(namePattern) && (schedule.Name ?? "").IndexOf(namePattern, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    var definition = schedule.Definition;
                    var categoryName = definition == null ? "<unknown>" : ScheduleHelpers.CategoryName(doc, definition) ?? "<unknown>";
                    var builtIn = definition == null ? null : ScheduleHelpers.BuiltInCategoryName(definition);
                    if (!string.IsNullOrEmpty(categoryFilter)
                        && categoryName.IndexOf(categoryFilter, StringComparison.OrdinalIgnoreCase) < 0
                        && (builtIn ?? "").IndexOf(categoryFilter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    rows.Add(new JObject
                    {
                        ["id"] = RevitCompat.GetId(schedule.Id),
                        ["name"] = schedule.Name,
                        ["categoryName"] = categoryName,
                        ["builtInCategory"] = builtIn,
                        ["isKey"] = definition?.IsKeySchedule ?? false,
                        ["isTitleblockRevisionSchedule"] = schedule.IsTitleblockRevisionSchedule,
                        ["isInternalKeynoteSchedule"] = schedule.IsInternalKeynoteSchedule,
                        ["isTemplate"] = schedule.IsTemplate,
                        ["fieldCount"] = definition?.GetFieldCount() ?? 0,
                        ["filterCount"] = definition?.GetFilterCount() ?? 0,
                        ["sortGroupCount"] = definition?.GetSortGroupFieldCount() ?? 0,
                        ["isItemized"] = definition?.IsItemized ?? false
                    });
                }
                catch (Exception)
                {
                    skipped++;
                }
            }

            return CommandResult.Ok(new JObject { ["total"] = rows.Count, ["skipped"] = skipped, ["schedules"] = rows });
        }
    }
}
