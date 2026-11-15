// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Schedules/CreateScheduleHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M (PRD: door, window and room area schedules): trimmed to parameter fields. The category
// is a BuiltInCategory name (OST_Doors) instead of a localised category name; a field is a BuiltInParameter
// name (ROOM_AREA) or a parameter name, as a string or {parameterName, columnHeading, hidden, total};
// total shows the column's totals in group footers and the grand total; showGrandTotals is new; phase is
// optional (rvt-mcp forced the active view's phase). Dropped: formula and combined fields (reflection over
// version-specific API), element-id filter values (rvt-mcp's ScheduleElementIdFilter), fieldType matching.
// Unknown keys inside fields/filters/sortGroup entries are rejected; misses list the schedulable fields.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Schedule
{
    public sealed class CreateScheduleHandler : IRevitCommand
    {
        private const int MaxOptions = 150;

        public string Name => "create_schedule";
        public string Description =>
            "Create a schedule. category: BuiltInCategory name (OST_Doors, OST_Windows, OST_Rooms). fields: BuiltInParameter or parameter names, "
            + "as strings or {parameterName, columnHeading, hidden, total}. filters: [{fieldName, operator, value}] (lengths in mm, areas in m2). "
            + "sortGroup: [{fieldName, order: ascending|descending, showHeader, showFooter, showBlankLine}]. isItemized, showGrandTotals, phase. "
            + "Area statement per flat: fields [ROOM_DEPARTMENT, ROOM_NUMBER, ROOM_NAME, {parameterName: ROOM_AREA, total: true}], "
            + "sortGroup [{fieldName: ROOM_DEPARTMENT, showHeader: true, showFooter: true}, {fieldName: ROOM_NUMBER}], showGrandTotals: true.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""category"":{""type"":""string""},
            ""name"":{""type"":""string""},
            ""fields"":{""type"":""array""},
            ""filters"":{""type"":""array"",""items"":{""type"":""object""}},
            ""sortGroup"":{""type"":""array"",""items"":{""type"":""object""}},
            ""isItemized"":{""type"":""boolean""},
            ""showGrandTotals"":{""type"":""boolean""},
            ""phase"":{""type"":""string""}},
            ""required"":[""category"",""name"",""fields""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var name = p.Value<string>("name");
            if (string.IsNullOrWhiteSpace(name)) return CommandResult.Fail("INVALID_PARAMS: name cannot be empty.");
            if (!Lookup.TryBuiltInCategory(p.Value<string>("category"), out var category, out var failure)) return failure;
            var fields = (JArray)p["fields"];
            if (fields.Count == 0) return CommandResult.Fail("INVALID_PARAMS: fields must list at least one field.");
            var filters = p["filters"] as JArray ?? new JArray();
            var sorts = p["sortGroup"] as JArray ?? new JArray();

            // Shape checks before the transaction (the schema cannot see inside array entries).
            for (var i = 0; i < fields.Count; i++)
                if (!(fields[i].Type == JTokenType.String || (fields[i] is JObject f && Keys(f, "fields[" + i + "]", out failure,
                        "parameterName", "columnHeading", "hidden", "total") && f.Value<string>("parameterName") != null)))
                    return failure ?? CommandResult.Fail("INVALID_PARAMS: fields[" + i + "] must be a name or {\"parameterName\": name, ...}.");
            for (var i = 0; i < filters.Count; i++)
                if (!Keys((JObject)filters[i], "filters[" + i + "]", out failure, "fieldName", "operator", "value")) return failure;
            for (var i = 0; i < sorts.Count; i++)
                if (!Keys((JObject)sorts[i], "sortGroup[" + i + "]", out failure,
                        "fieldName", "ascending", "order", "sortOrder", "showHeader", "showFooter", "showBlankLine")) return failure;

            var clash = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (clash != null)
                return CommandResult.Fail("NAME_IN_USE: a schedule named '" + clash.Name + "' already exists.",
                    new { existingScheduleId = RevitCompat.GetId(clash.Id) });

            Phase phase = null;
            var phaseName = p.Value<string>("phase");
            if (!string.IsNullOrEmpty(phaseName))
            {
                var phases = new FilteredElementCollector(doc).OfClass(typeof(Phase)).Cast<Phase>().ToList();
                phase = phases.FirstOrDefault(ph => string.Equals(ph.Name, phaseName, StringComparison.OrdinalIgnoreCase));
                if (phase == null)
                    return CommandResult.Fail("PHASE_NOT_FOUND: no phase named '" + phaseName + "'.",
                        new JObject { ["valid_phases"] = new JArray(phases.Select(ph => ph.Name)) });
            }

            using (var tx = new Transaction(doc, "B2M: Create Schedule"))
            {
                tx.Start();
                try
                {
                    var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(category));
                    schedule.Name = name;
                    var definition = schedule.Definition;
                    var schedulable = definition.GetSchedulableFields();
                    if (phase != null)
                    {
                        var phaseParam = schedule.get_Parameter(BuiltInParameter.VIEW_PHASE);
                        if (phaseParam == null || phaseParam.IsReadOnly) throw new ArgumentException("this schedule has no settable phase.");
                        phaseParam.Set(phase.Id);
                    }

                    // Every way the caller may refer to a field later: the key given, its name, its heading.
                    var byKey = new Dictionary<string, ScheduleField>(StringComparer.OrdinalIgnoreCase);
                    var added = new JArray();
                    foreach (var entry in fields)
                    {
                        var spec = entry as JObject;
                        var key = spec == null ? entry.Value<string>() : spec.Value<string>("parameterName");
                        var match = FindField(doc, schedulable, key);
                        if (match == null)
                        {
                            tx.RollBack();
                            return CommandResult.Fail("FIELD_NOT_FOUND: " + category + " schedules have no field '" + key + "'. Nothing was created.",
                                new JObject { ["valid_fields"] = Describe(doc, schedulable) });
                        }
                        var field = definition.AddField(match);
                        var heading = spec?.Value<string>("columnHeading");
                        if (!string.IsNullOrEmpty(heading)) field.ColumnHeading = heading;
                        if (spec?.Value<bool?>("hidden") == true) field.IsHidden = true;
                        if (spec?.Value<bool?>("total") == true)
                        {
                            if (!field.CanTotal()) throw new ArgumentException("field '" + key + "' cannot be totalled.");
                            field.DisplayType = ScheduleFieldDisplayType.Totals;
                        }
                        foreach (var alias in new[] { key, field.GetName(), field.ColumnHeading })
                            if (!string.IsNullOrEmpty(alias) && !byKey.ContainsKey(alias)) byKey[alias] = field;
                        added.Add(new JObject
                        {
                            ["parameterName"] = key,
                            ["name"] = field.GetName(),
                            ["columnHeading"] = field.ColumnHeading,
                            ["builtInParameter"] = BuiltInName(match.ParameterId),
                            ["hidden"] = field.IsHidden,
                            ["total"] = field.DisplayType == ScheduleFieldDisplayType.Totals
                        });
                    }

                    for (var i = 0; i < filters.Count; i++)
                    {
                        var spec = (JObject)filters[i];
                        var field = Resolve(byKey, spec.Value<string>("fieldName"), "filters[" + i + "]");
                        definition.AddFilter(Filter(field, Operator(spec.Value<string>("operator")), spec["value"]));
                    }

                    for (var i = 0; i < sorts.Count; i++)
                    {
                        var spec = (JObject)sorts[i];
                        var field = Resolve(byKey, spec.Value<string>("fieldName"), "sortGroup[" + i + "]");
                        var sort = new ScheduleSortGroupField(field.FieldId,
                            ScheduleSortInput.Ascending(spec) ? ScheduleSortOrder.Ascending : ScheduleSortOrder.Descending);
                        if (spec.Value<bool?>("showHeader") is bool header) sort.ShowHeader = header;
                        if (spec.Value<bool?>("showFooter") is bool footer)
                        {
                            sort.ShowFooter = footer;
                            sort.ShowFooterTitle = footer;
                        }
                        if (spec.Value<bool?>("showBlankLine") is bool blank) sort.ShowBlankLine = blank;
                        definition.AddSortGroupField(sort);
                    }

                    if (p.Value<bool?>("isItemized") is bool itemized) definition.IsItemized = itemized;
                    if (p.Value<bool?>("showGrandTotals") is bool grand)
                    {
                        definition.ShowGrandTotal = grand;
                        definition.ShowGrandTotalTitle = grand;
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    return CommandResult.Ok(new
                    {
                        scheduleId = RevitCompat.GetId(schedule.Id),
                        scheduleName = schedule.Name,
                        category = category.ToString(),
                        phaseName = (doc.GetElement(schedule.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() ?? ElementId.InvalidElementId) as Phase)?.Name,
                        fields = added,
                        filtersApplied = filters.Count,
                        sortGroupApplied = sorts.Count,
                        isItemized = definition.IsItemized,
                        showGrandTotals = definition.ShowGrandTotal
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the schedule: " + ex.Message + " Nothing was created.");
                }
            }
        }

        /// <summary>By BuiltInParameter name, else by field name; an instance field wins over a type field of the same name.</summary>
        private static SchedulableField FindField(Document doc, IList<SchedulableField> all, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            IEnumerable<SchedulableField> matches;
            if (char.IsLetter(key[0]) && Enum.TryParse(key, false, out BuiltInParameter bip) && Enum.IsDefined(typeof(BuiltInParameter), bip))
            {
                var id = new ElementId(bip);
                matches = all.Where(sf => sf.ParameterId == id);
            }
            else
            {
                matches = all.Where(sf => string.Equals(sf.GetName(doc), key, StringComparison.OrdinalIgnoreCase));
            }
            return matches.OrderBy(sf => sf.FieldType == ScheduleFieldType.Instance ? 0 : sf.FieldType == ScheduleFieldType.ElementType ? 1 : 2)
                .FirstOrDefault();
        }

        private static ScheduleField Resolve(Dictionary<string, ScheduleField> byKey, string key, string where)
        {
            if (!string.IsNullOrEmpty(key) && byKey.TryGetValue(key, out var field)) return field;
            throw new ArgumentException(where + ".fieldName '" + key + "' is not one of the schedule's fields ("
                + string.Join(", ", byKey.Keys) + ").");
        }

        private static ScheduleFilterType Operator(string op)
        {
            switch ((op ?? "").Trim().ToLowerInvariant())
            {
                case "equals": case "equal": case "=": return ScheduleFilterType.Equal;
                case "notequal": case "notequals": case "!=": return ScheduleFilterType.NotEqual;
                case "greaterthan": case ">": return ScheduleFilterType.GreaterThan;
                case "greaterthanorequal": case ">=": return ScheduleFilterType.GreaterThanOrEqual;
                case "lessthan": case "<": return ScheduleFilterType.LessThan;
                case "lessthanorequal": case "<=": return ScheduleFilterType.LessThanOrEqual;
                case "contains": return ScheduleFilterType.Contains;
                case "notcontains": return ScheduleFilterType.NotContains;
                case "beginswith": return ScheduleFilterType.BeginsWith;
                case "endswith": return ScheduleFilterType.EndsWith;
                case "hasvalue": return ScheduleFilterType.HasValue;
                case "hasnovalue": return ScheduleFilterType.HasNoValue;
                default:
                    throw new ArgumentException("operator '" + op + "' is not supported. Use equals, notEqual, greaterThan, greaterThanOrEqual, "
                        + "lessThan, lessThanOrEqual, contains, notContains, beginsWith, endsWith, hasValue, hasNoValue.");
            }
        }

        /// <summary>Numbers on length fields are mm, on area fields m2, on volume fields m3; other values pass through.</summary>
        private static ScheduleFilter Filter(ScheduleField field, ScheduleFilterType type, JToken value)
        {
            if (type == ScheduleFilterType.HasValue || type == ScheduleFilterType.HasNoValue)
                return new ScheduleFilter(field.FieldId, type);
            if (value == null || value.Type == JTokenType.Null)
                throw new ArgumentException("filter on '" + field.GetName() + "' needs a value.");

            var spec = field.GetSpecTypeId();
            var measurable = spec != null && !spec.Empty() && UnitUtils.IsMeasurableSpec(spec);
            if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float)
            {
                var number = value.Value<double>();
                if (!measurable) return new ScheduleFilter(field.FieldId, type, value.Value<int>());
                if (spec == SpecTypeId.Length) number = Mm.ToFeet(number);
                else if (spec == SpecTypeId.Area) number = UnitUtils.ConvertToInternalUnits(number, UnitTypeId.SquareMeters);
                else if (spec == SpecTypeId.Volume) number = UnitUtils.ConvertToInternalUnits(number, UnitTypeId.CubicMeters);
                return new ScheduleFilter(field.FieldId, type, number);
            }
            if (value.Type == JTokenType.Boolean) return new ScheduleFilter(field.FieldId, type, value.Value<bool>() ? 1 : 0);
            if (value.Type == JTokenType.String) return new ScheduleFilter(field.FieldId, type, value.Value<string>());
            throw new ArgumentException("filter value on '" + field.GetName() + "' must be a string, number or boolean.");
        }

        private static bool Keys(JObject entry, string where, out CommandResult failure, params string[] allowed)
        {
            var unknown = entry.Properties().Select(x => x.Name).Where(n => !allowed.Contains(n)).ToList();
            failure = unknown.Count == 0 ? null
                : CommandResult.Fail("INVALID_PARAMS: unknown key(s) " + string.Join(", ", unknown) + " in " + where
                    + ". Valid keys: " + string.Join(", ", allowed) + ".");
            return failure == null;
        }

        private static string BuiltInName(ElementId parameterId)
        {
            var value = parameterId == null ? 0 : RevitCompat.GetId(parameterId);
            return value < 0 && Enum.IsDefined(typeof(BuiltInParameter), (int)value) ? ((BuiltInParameter)(int)value).ToString() : null;
        }

        private static JArray Describe(Document doc, IEnumerable<SchedulableField> all) =>
            new JArray(all.Take(MaxOptions).Select(sf => new JObject
            {
                ["name"] = sf.GetName(doc),
                ["builtInParameter"] = BuiltInName(sf.ParameterId),
                ["fieldType"] = sf.FieldType.ToString()
            }));
    }
}
