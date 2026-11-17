// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Schedules/GetSchedulableFieldsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: schedule misses list the valid schedules; kindFilter values are checked against
// ScheduleFieldType names (an unknown kind is an error, rvt-mcp returned nothing); specTypeId comes from the
// project/shared parameter definition (rvt-mcp reflected for SchedulableField.GetSpecTypeId, which the
// 2024-2027 API does not have, so it was always null) and stays null for built-in parameters;
// builtInParameter (e.g. ROOM_AREA) is added so the name can be passed to create_schedule.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Schedule
{
    public sealed class GetSchedulableFieldsHandler : IRevitCommand
    {
        public string Name => "get_schedulable_fields";
        public string Description =>
            "List the parameters that can be added to a schedule (scheduleId or scheduleName) and are not in it yet; "
            + "the step before add_schedule_field. Optional kindFilter: field types such as Instance, ElementType, Count, Room.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + ScheduleHelpers.ScheduleRefSchema + @",
            ""kindFilter"":{""type"":""array"",""items"":{""type"":""string""}}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!ScheduleHelpers.TryResolve(doc, p, out var schedule, out var failure)) return failure;
            var definition = schedule.Definition;

            HashSet<string> kinds = null;
            if (p["kindFilter"] is JArray kindArray && kindArray.Count > 0)
            {
                var valid = Enum.GetNames(typeof(ScheduleFieldType));
                kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kind in kindArray.Select(t => t.Value<string>()))
                {
                    if (!valid.Contains(kind, StringComparer.OrdinalIgnoreCase))
                        return CommandResult.Fail("INVALID_PARAMS: kindFilter value '" + kind + "' is not a field type.",
                            new JObject { ["valid_kinds"] = new JArray(valid) });
                    kinds.Add(kind);
                }
            }

            var added = new HashSet<string>(StringComparer.Ordinal);
            var order = definition.GetFieldOrder();
            foreach (var id in order)
            {
                var field = definition.GetField(id);
                if (!field.HasSchedulableField) continue;
                var sf = field.GetSchedulableField();
                added.Add(Key(sf));
            }

            var all = definition.GetSchedulableFields();
            var rows = new List<JObject>();
            var skipped = 0;
            foreach (var sf in all)
            {
                try
                {
                    if (added.Contains(Key(sf))) continue;
                    var kind = sf.FieldType.ToString();
                    if (kinds != null && !kinds.Contains(kind)) continue;
                    var paramElement = sf.ParameterId == ElementId.InvalidElementId ? null : doc.GetElement(sf.ParameterId) as ParameterElement;
                    string spec = null;
                    try { spec = paramElement?.GetDefinition()?.GetDataType()?.TypeId; }
                    catch (Exception) { }
                    var source = ScheduleHelpers.ParameterSource(doc, sf.ParameterId);
                    rows.Add(new JObject
                    {
                        ["parameterName"] = sf.GetName(doc),
                        ["parameterId"] = RevitCompat.GetIdOrNull(sf.ParameterId),
                        ["builtInParameter"] = ScheduleHelpers.BuiltInParameterName(sf.ParameterId),
                        ["fieldType"] = kind,
                        ["specTypeId"] = string.IsNullOrEmpty(spec) ? null : spec,
                        ["source"] = source,
                        ["isShared"] = source == "Shared"
                    });
                }
                catch (Exception)
                {
                    skipped++;
                }
            }

            var sorted = rows.OrderBy(r => r.Value<string>("parameterName") ?? "", StringComparer.OrdinalIgnoreCase).ToList();
            return CommandResult.Ok(new JObject
            {
                ["scheduleId"] = RevitCompat.GetId(schedule.Id),
                ["scheduleName"] = schedule.Name,
                ["total"] = all.Count,
                ["alreadyAdded"] = added.Count,
                ["available"] = sorted.Count,
                ["skipped"] = skipped,
                ["fields"] = new JArray(sorted)
            });
        }

        private static string Key(SchedulableField sf) =>
            (RevitCompat.GetIdOrNull(sf.ParameterId)?.ToString() ?? "null") + "|" + sf.FieldType;
    }
}
