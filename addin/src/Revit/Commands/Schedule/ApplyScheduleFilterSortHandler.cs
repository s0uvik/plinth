// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Schedules/ApplyScheduleFilterSortHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: every entry is checked (shape, unknown keys, field, operator, value) before the transaction,
// so a bad entry no longer leaves a half-applied schedule; a field miss lists the schedule's fields; sort order
// accepts ascending/order/sortOrder through ScheduleSortInput like create_schedule; showFooter also shows the
// footer title; showGrandTotal also shows its title; filter numbers on length/area/volume/angle fields are
// mm/m2/m3/degrees through ScheduleHelpers; element-reference values (rvt-mcp's ScheduleElementIdFilter) are
// kept; the "hasNoParameter" operator is dropped (the 2024-2027 API has no such filter type, rvt-mcp fell
// back to HasNoValue); applied through SetFilters/SetSortGroupFields in one transaction.

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Schedule
{
    public sealed class ApplyScheduleFilterSortHandler : IRevitCommand
    {
        private static readonly string[] SettingKeys = { "isItemized", "showGrandTotal", "showHeaders", "showTitle" };

        public string Name => "apply_schedule_filter_sort";
        public string Description =>
            "Replace a schedule's filters and/or sort-group entries and change settings (scheduleId or scheduleName). Sections left out are kept; "
            + "an empty array clears that section. filters: [{fieldName, operator, value}] (operators: " + ScheduleHelpers.FilterOperators
            + "; lengths mm, areas m2, volumes m3, angles degrees; element parameters such as Phase or Level take an element id or name). "
            + "sortGroup: [{fieldName, order: ascending|descending, showHeader, showFooter, showBlankLine}]. "
            + "settings: {isItemized, showGrandTotal, showHeaders, showTitle}.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + ScheduleHelpers.ScheduleRefSchema + @",
            ""filters"":{""type"":""array"",""items"":{""type"":""object""}},
            ""sortGroup"":{""type"":""array"",""items"":{""type"":""object""}},
            ""settings"":{""type"":""object""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var filterSpecs = p["filters"] as JArray;
            var sortSpecs = p["sortGroup"] as JArray;
            var settings = p["settings"] as JObject;
            if (filterSpecs == null && sortSpecs == null && settings == null)
                return CommandResult.Fail("INVALID_PARAMS: pass filters, sortGroup or settings.");
            CommandResult failure;
            if (settings != null)
            {
                if (!ScheduleHelpers.Keys(settings, "settings", out failure, SettingKeys)) return failure;
                foreach (var s in settings.Properties())
                    if (s.Value.Type != JTokenType.Boolean)
                        return CommandResult.Fail("INVALID_PARAMS: settings." + s.Name + " must be true or false.");
            }
            if (!ScheduleHelpers.TryResolve(doc, p, out var schedule, out failure)) return failure;
            var definition = schedule.Definition;

            // Build every filter and sort entry before the transaction.
            List<ScheduleFilter> filters = null;
            if (filterSpecs != null)
            {
                filters = new List<ScheduleFilter>();
                for (var i = 0; i < filterSpecs.Count; i++)
                {
                    var at = "filters[" + i + "]";
                    var spec = (JObject)filterSpecs[i];
                    if (!ScheduleHelpers.Keys(spec, at, out failure, "fieldName", "operator", "value")) return failure;
                    if (!ScheduleHelpers.TryField(definition, null, spec.Value<string>("fieldName"), out var field, out _, out failure))
                        return failure;
                    if (!ScheduleHelpers.TryFilterType(spec.Value<string>("operator"), out var type))
                        return CommandResult.Fail("INVALID_PARAMS: " + at + ".operator '" + spec.Value<string>("operator") + "' is not supported. Use "
                            + ScheduleHelpers.FilterOperators + ".");
                    try { filters.Add(ScheduleHelpers.BuildFilter(doc, schedule, field, type, spec["value"])); }
                    catch (Exception ex) { return CommandResult.Fail("INVALID_FILTER: " + at + ": " + ex.Message); }
                }
            }

            List<ScheduleSortGroupField> sorts = null;
            if (sortSpecs != null)
            {
                sorts = new List<ScheduleSortGroupField>();
                for (var i = 0; i < sortSpecs.Count; i++)
                {
                    var at = "sortGroup[" + i + "]";
                    var spec = (JObject)sortSpecs[i];
                    if (!ScheduleHelpers.Keys(spec, at, out failure,
                            "fieldName", "ascending", "order", "sortOrder", "showHeader", "showFooter", "showBlankLine")) return failure;
                    if (!ScheduleHelpers.TryField(definition, null, spec.Value<string>("fieldName"), out var field, out _, out failure))
                        return failure;
                    if (!definition.CanSortByField(field.FieldId))
                        return CommandResult.Fail("CANNOT_SORT: " + at + ": the schedule cannot sort by '" + field.GetName() + "'.");
                    bool ascending;
                    try { ascending = ScheduleSortInput.Ascending(spec); }
                    catch (ArgumentException ex) { return CommandResult.Fail("INVALID_PARAMS: " + at + ": " + ex.Message); }
                    foreach (var key in new[] { "showHeader", "showFooter", "showBlankLine" })
                        if (spec[key] != null && spec[key].Type != JTokenType.Boolean)
                            return CommandResult.Fail("INVALID_PARAMS: " + at + "." + key + " must be true or false.");
                    var sort = new ScheduleSortGroupField(field.FieldId, ascending ? ScheduleSortOrder.Ascending : ScheduleSortOrder.Descending);
                    if (spec.Value<bool?>("showHeader") is bool header) sort.ShowHeader = header;
                    if (spec.Value<bool?>("showFooter") is bool footer)
                    {
                        sort.ShowFooter = footer;
                        sort.ShowFooterTitle = footer;
                    }
                    if (spec.Value<bool?>("showBlankLine") is bool blank) sort.ShowBlankLine = blank;
                    sorts.Add(sort);
                }
            }

            int filtersCleared = 0, sortCleared = 0, settingsApplied = 0;
            using (var tx = new Transaction(doc, "Plinth: Apply Schedule Filter/Sort"))
            {
                tx.Start();
                try
                {
                    if (filters != null)
                    {
                        filtersCleared = definition.GetFilterCount();
                        definition.SetFilters(filters);
                    }
                    if (sorts != null)
                    {
                        sortCleared = definition.GetSortGroupFieldCount();
                        definition.SetSortGroupFields(sorts);
                    }
                    if (settings != null)
                    {
                        foreach (var s in settings.Properties())
                        {
                            var on = s.Value.Value<bool>();
                            switch (s.Name)
                            {
                                case "isItemized": definition.IsItemized = on; break;
                                case "showGrandTotal":
                                    definition.ShowGrandTotal = on;
                                    definition.ShowGrandTotalTitle = on;
                                    break;
                                case "showHeaders": definition.ShowHeaders = on; break;
                                case "showTitle": definition.ShowTitle = on; break;
                            }
                            settingsApplied++;
                        }
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("APPLY_FAILED: could not change the schedule: " + ex.Message + " Nothing was changed.");
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["scheduleId"] = RevitCompat.GetId(schedule.Id),
                ["scheduleName"] = schedule.Name,
                ["filtersCleared"] = filtersCleared,
                ["filtersApplied"] = filters?.Count ?? 0,
                ["sortGroupCleared"] = sortCleared,
                ["sortGroupApplied"] = sorts?.Count ?? 0,
                ["settingsApplied"] = settingsApplied,
                ["filterCount"] = definition.GetFilterCount(),
                ["sortGroupCount"] = definition.GetSortGroupFieldCount()
            });
        }
    }
}
