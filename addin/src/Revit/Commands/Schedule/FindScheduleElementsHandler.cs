// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Schedules/FindScheduleElementsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: schedule misses list the valid schedules; parameter values convert through ScheduleHelpers
// (length mm via Mm, area m2, volume m3, angle degrees through UnitUtils; rvt-mcp also treated pipe/duct/bar
// sizes as lengths, kept here) and print with the invariant culture; builtInCategory is added per element.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Schedule
{
    public sealed class FindScheduleElementsHandler : IRevitCommand
    {
        private const int MaxParametersPerElement = 30;
        private const int MaxLimit = 500;

        public string Name => "find_schedule_elements";
        public string Description =>
            "List the model elements a schedule shows (scheduleId or scheduleName): count per category and per element id, name, "
            + "category and type. includeParameters adds up to 30 parameter values per element (lengths mm, areas m2, volumes m3, "
            + "angles degrees). limit 1-500 (default 500).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + ScheduleHelpers.ScheduleRefSchema + @",
            ""groupByCategory"":{""type"":""boolean""},
            ""includeParameters"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var groupByCategory = p.Value<bool?>("groupByCategory") ?? true;
            var includeParameters = p.Value<bool?>("includeParameters") ?? false;
            var limit = p.Value<int?>("limit") ?? MaxLimit;
            if (limit < 1 || limit > MaxLimit) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to " + MaxLimit + ".");
            if (!ScheduleHelpers.TryResolve(doc, p, out var schedule, out var failure)) return failure;
            if (schedule.IsTemplate)
                return CommandResult.Fail("SCHEDULE_IS_TEMPLATE: '" + schedule.Name + "' is a schedule template and shows no elements.");

            // A collector scoped to the schedule's id returns the elements the schedule shows.
            var all = new FilteredElementCollector(doc, schedule.Id).ToElements();

            JObject byCategory = null;
            if (groupByCategory)
            {
                byCategory = new JObject();
                foreach (var group in all.GroupBy(e => e.Category?.Name ?? "<none>").OrderBy(g => g.Key))
                    byCategory[group.Key] = group.Count();
            }

            var elements = new JArray();
            foreach (var el in all.Take(limit))
            {
                try
                {
                    var row = new JObject
                    {
                        ["id"] = RevitCompat.GetId(el.Id),
                        ["name"] = el.Name ?? "",
                        ["categoryName"] = el.Category?.Name ?? "<none>",
                        ["builtInCategory"] = Lookup.BuiltInName(el.Category),
                        ["typeName"] = doc.GetElement(el.GetTypeId())?.Name ?? ""
                    };
                    if (includeParameters) row["parameters"] = Parameters(doc, el);
                    elements.Add(row);
                }
                catch (Exception ex)
                {
                    elements.Add(new JObject { ["id"] = RevitCompat.GetId(el.Id), ["error"] = ex.Message });
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["scheduleId"] = RevitCompat.GetId(schedule.Id),
                ["scheduleName"] = schedule.Name,
                ["totalCount"] = all.Count,
                ["returned"] = elements.Count,
                ["truncated"] = all.Count > limit,
                ["byCategory"] = byCategory,
                ["elements"] = elements
            });
        }

        private static JObject Parameters(Document doc, Element el)
        {
            var map = new JObject();
            foreach (Parameter param in el.Parameters)
            {
                if (map.Count >= MaxParametersPerElement) break;
                if (param?.Definition == null || !param.HasValue) continue;
                try
                {
                    var name = param.Definition.Name;
                    if (string.IsNullOrEmpty(name) || map[name] != null) continue;
                    var value = ValueText(doc, param);
                    if (value != null) map[name] = value;
                }
                catch (Exception) { }
            }
            return map;
        }

        private static string ValueText(Document doc, Parameter param)
        {
            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString();
                case StorageType.Integer:
                    return param.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture);
                case StorageType.Double:
                    ForgeTypeId spec = null;
                    try { spec = param.Definition.GetDataType(); }
                    catch (Exception) { }
                    var raw = param.AsDouble();
                    var isSize = spec != null && (spec == SpecTypeId.PipeSize || spec == SpecTypeId.DuctSize || spec == SpecTypeId.BarDiameter
                        || spec == SpecTypeId.WireDiameter || spec == SpecTypeId.SectionDimension);
                    var value = isSize ? Mm.FromFeet(raw, 3) : ScheduleHelpers.ToDisplay(raw, spec);
                    return Math.Round(value, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
                case StorageType.ElementId:
                    var id = param.AsElementId();
                    return doc.GetElement(id)?.Name ?? RevitCompat.GetId(id).ToString(System.Globalization.CultureInfo.InvariantCulture);
                default:
                    return null;
            }
        }
    }
}
