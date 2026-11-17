// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), files
// src/shared/Handlers/Schedules/ScheduleElementIdFilter.cs and the schedule lookup, unit, filter-operator and
// combined-parameter code repeated in src/shared/Handlers/Schedules/*Handler.cs, commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for Plinth: one copy of what rvt-mcp repeated per handler. Schedule misses list the valid schedules
// and a name shared by several schedules lists them; units go through Mm and UnitUtils (length mm, area m2,
// volume m3, angle degrees); combined parameters use the public TableCellCombinedParameterData API instead
// of reflection (Truncated/NumberOfCharacters do not exist in the 2024-2027 API and are not offered);
// unknown keys in nested objects are rejected.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Schedule
{
    internal static class ScheduleHelpers
    {
        private const int MaxOptions = 100;

        public const string ScheduleRefSchema = @"""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""}";

        public const string FilterOperators =
            "equals, notEqual, greaterThan, greaterThanOrEqual, lessThan, lessThanOrEqual, contains, notContains, "
            + "beginsWith, notBeginsWith, endsWith, notEndsWith, hasValue, hasNoValue, hasParameter";

        /// <summary>Any schedule view (templates and revision schedules included) by scheduleId, else by exact name.</summary>
        public static bool TryResolve(Document doc, JObject p, out ViewSchedule schedule, out CommandResult failure)
        {
            schedule = null;
            failure = null;
            var id = p.Value<long?>("scheduleId");
            var name = p.Value<string>("scheduleName");
            if (id.HasValue && !string.IsNullOrEmpty(name))
            {
                failure = CommandResult.Fail("INVALID_PARAMS: pass scheduleId or scheduleName, not both.");
                return false;
            }
            var all = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .OrderBy(s => s.Name).ToList();
            List<ViewSchedule> matches;
            string miss;
            if (id.HasValue)
            {
                matches = all.Where(s => RevitCompat.GetId(s.Id) == id.Value).ToList();
                miss = "no schedule has id " + id.Value + ".";
            }
            else if (!string.IsNullOrWhiteSpace(name))
            {
                matches = all.Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                miss = "no schedule named '" + name + "'.";
            }
            else
            {
                matches = new List<ViewSchedule>();
                miss = "give scheduleId or scheduleName.";
            }
            if (matches.Count == 1)
            {
                schedule = matches[0];
                return true;
            }
            failure = matches.Count > 1
                ? CommandResult.Fail("SCHEDULE_AMBIGUOUS: " + matches.Count + " schedules are named '" + name + "'; pass scheduleId.",
                    new JObject { ["matching_schedules"] = Options(matches) })
                : CommandResult.Fail("SCHEDULE_NOT_FOUND: " + miss, new JObject { ["valid_schedules"] = Options(all) });
            return false;
        }

        private static JArray Options(IEnumerable<ViewSchedule> schedules) =>
            new JArray(schedules.Where(s => !s.IsTitleblockRevisionSchedule).Take(MaxOptions).Select(s => new JObject
            {
                ["id"] = RevitCompat.GetId(s.Id),
                ["name"] = s.Name,
                ["isTemplate"] = s.IsTemplate
            }));

        /// <summary>A field of the schedule by its position (0-based, as get_schedule_definition reports) or by name or heading.</summary>
        public static bool TryField(ScheduleDefinition definition, int? index, string name, out ScheduleField field, out int fieldIndex,
            out CommandResult failure)
        {
            field = null;
            fieldIndex = -1;
            failure = null;
            var order = definition.GetFieldOrder();
            if (index.HasValue)
            {
                if (index.Value >= 0 && index.Value < order.Count)
                {
                    fieldIndex = index.Value;
                    field = definition.GetField(order[index.Value]);
                }
            }
            else if (!string.IsNullOrWhiteSpace(name))
            {
                for (var i = 0; i < order.Count && field == null; i++)
                {
                    var candidate = definition.GetField(order[i]);
                    if (string.Equals(candidate.GetName(), name, StringComparison.OrdinalIgnoreCase)) { field = candidate; fieldIndex = i; }
                }
                for (var i = 0; i < order.Count && field == null; i++)
                {
                    var candidate = definition.GetField(order[i]);
                    if (string.Equals(candidate.ColumnHeading, name, StringComparison.OrdinalIgnoreCase)) { field = candidate; fieldIndex = i; }
                }
            }
            if (field != null) return true;
            failure = CommandResult.Fail("FIELD_NOT_FOUND: " + (index.HasValue
                    ? "the schedule has no field at index " + index.Value + " (it has " + order.Count + ")."
                    : string.IsNullOrWhiteSpace(name) ? "give fieldIndex or fieldName." : "the schedule has no field named '" + name + "'."),
                new JObject { ["valid_fields"] = FieldList(definition) });
            return false;
        }

        public static JArray FieldList(ScheduleDefinition definition)
        {
            var order = definition.GetFieldOrder();
            var list = new JArray();
            for (var i = 0; i < order.Count; i++)
            {
                var f = definition.GetField(order[i]);
                list.Add(new JObject { ["fieldIndex"] = i, ["name"] = f.GetName(), ["columnHeading"] = f.ColumnHeading });
            }
            return list;
        }

        public static int IndexOf(ScheduleDefinition definition, ScheduleFieldId id)
        {
            var order = definition.GetFieldOrder();
            for (var i = 0; i < order.Count; i++)
                if (order[i] == id) return i;
            return -1;
        }

        /// <summary>Localised category name, "&lt;multi-category&gt;" or null.</summary>
        public static string CategoryName(Document doc, ScheduleDefinition definition)
        {
            if (definition.CategoryId == ElementId.InvalidElementId) return "<multi-category>";
            try { return Category.GetCategory(doc, definition.CategoryId)?.Name; }
            catch (Exception) { return null; }
        }

        public static string BuiltInCategoryName(ScheduleDefinition definition)
        {
            var id = RevitCompat.GetId(definition.CategoryId);
            return id < 0 && id >= int.MinValue && Enum.IsDefined(typeof(BuiltInCategory), (int)id) ? ((BuiltInCategory)(int)id).ToString() : null;
        }

        /// <summary>The BuiltInParameter name (ROOM_AREA) of a parameter id, or null for project/shared parameters.</summary>
        public static string BuiltInParameterName(ElementId parameterId)
        {
            var value = parameterId == null ? 0 : RevitCompat.GetId(parameterId);
            return value < 0 && value >= int.MinValue && Enum.IsDefined(typeof(BuiltInParameter), (int)value)
                ? ((BuiltInParameter)(int)value).ToString() : null;
        }

        /// <summary>A parameter's display name: the parameter element's name, or the built-in parameter's label.</summary>
        public static string ParameterName(Document doc, ElementId parameterId)
        {
            if (parameterId == null || parameterId == ElementId.InvalidElementId) return null;
            var element = doc.GetElement(parameterId);
            if (element != null && !string.IsNullOrEmpty(element.Name)) return element.Name;
            var bip = BuiltInParameterName(parameterId);
            if (bip == null) return null;
            try { return LabelUtils.GetLabelFor((BuiltInParameter)Enum.Parse(typeof(BuiltInParameter), bip)); }
            catch (Exception) { return bip; }
        }

        /// <summary>"BuiltIn", "Shared" or "Project".</summary>
        public static string ParameterSource(Document doc, ElementId parameterId)
        {
            var element = parameterId == null || parameterId == ElementId.InvalidElementId ? null : doc.GetElement(parameterId);
            return element is SharedParameterElement ? "Shared" : element is ParameterElement ? "Project" : "BuiltIn";
        }

        // ---- units: length mm, area m2, volume m3, angle degrees; everything else unchanged ----

        public static double ToDisplay(double internalValue, ForgeTypeId spec)
        {
            if (spec == null) return internalValue;
            if (spec == SpecTypeId.Length) return Mm.FromFeet(internalValue, 3);
            if (spec == SpecTypeId.Area) return UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.SquareMeters);
            if (spec == SpecTypeId.Volume) return UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.CubicMeters);
            if (spec == SpecTypeId.Angle) return UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.Degrees);
            return internalValue;
        }

        public static double ToInternal(double displayValue, ForgeTypeId spec)
        {
            if (spec == null) return displayValue;
            if (spec == SpecTypeId.Length) return Mm.ToFeet(displayValue);
            if (spec == SpecTypeId.Area) return UnitUtils.ConvertToInternalUnits(displayValue, UnitTypeId.SquareMeters);
            if (spec == SpecTypeId.Volume) return UnitUtils.ConvertToInternalUnits(displayValue, UnitTypeId.CubicMeters);
            if (spec == SpecTypeId.Angle) return UnitUtils.ConvertToInternalUnits(displayValue, UnitTypeId.Degrees);
            return displayValue;
        }

        public static string UnitLabel(ForgeTypeId spec)
        {
            if (spec == null) return null;
            if (spec == SpecTypeId.Length) return "mm";
            if (spec == SpecTypeId.Area) return "m2";
            if (spec == SpecTypeId.Volume) return "m3";
            if (spec == SpecTypeId.Angle) return "degrees";
            return null;
        }

        public static string Format(double value) => Math.Round(value, 3).ToString(CultureInfo.InvariantCulture);

        public static ForgeTypeId SpecOf(ScheduleField field)
        {
            try
            {
                var spec = field.GetSpecTypeId();
                return spec == null || spec.Empty() ? null : spec;
            }
            catch (Exception) { return null; }
        }

        // ---- filters ----

        public static bool TryFilterType(string op, out ScheduleFilterType type)
        {
            type = ScheduleFilterType.Invalid;
            switch ((op ?? "").Trim().ToLowerInvariant())
            {
                case "equals": case "equal": case "=": type = ScheduleFilterType.Equal; break;
                case "notequal": case "notequals": case "!=": case "<>": type = ScheduleFilterType.NotEqual; break;
                case "greaterthan": case "greater": case ">": type = ScheduleFilterType.GreaterThan; break;
                case "greaterthanorequal": case "greaterorequal": case ">=": type = ScheduleFilterType.GreaterThanOrEqual; break;
                case "lessthan": case "less": case "<": type = ScheduleFilterType.LessThan; break;
                case "lessthanorequal": case "lessorequal": case "<=": type = ScheduleFilterType.LessThanOrEqual; break;
                case "contains": type = ScheduleFilterType.Contains; break;
                case "notcontains": case "doesnotcontain": type = ScheduleFilterType.NotContains; break;
                case "beginswith": case "startswith": type = ScheduleFilterType.BeginsWith; break;
                case "notbeginswith": case "doesnotbeginwith": type = ScheduleFilterType.NotBeginsWith; break;
                case "endswith": type = ScheduleFilterType.EndsWith; break;
                case "notendswith": case "doesnotendwith": type = ScheduleFilterType.NotEndsWith; break;
                case "hasvalue": case "isnotempty": type = ScheduleFilterType.HasValue; break;
                case "hasnovalue": case "isempty": type = ScheduleFilterType.HasNoValue; break;
                case "hasparameter": type = ScheduleFilterType.HasParameter; break;
                default: return false;
            }
            return true;
        }

        /// <summary>
        /// A filter on one field. Element-reference parameters (phase, level, type...) take an element id or an exact
        /// element name; numbers on length/area/volume/angle fields are mm/m2/m3/degrees.
        /// </summary>
        public static ScheduleFilter BuildFilter(Document doc, ViewSchedule schedule, ScheduleField field, ScheduleFilterType type, JToken value)
        {
            if (type == ScheduleFilterType.HasValue || type == ScheduleFilterType.HasNoValue || type == ScheduleFilterType.HasParameter)
                return new ScheduleFilter(field.FieldId, type);
            if (value == null || value.Type == JTokenType.Null)
                throw new ArgumentException("a value is required for operator " + type + " on '" + field.GetName() + "'.");

            var elementId = ResolveElementIdValue(doc, schedule, field, value);
            if (elementId != null) return new ScheduleFilter(field.FieldId, type, elementId);

            var spec = SpecOf(field);
            var isDouble = spec != null && (spec == SpecTypeId.Length || spec == SpecTypeId.Area || spec == SpecTypeId.Volume
                || spec == SpecTypeId.Angle || spec == SpecTypeId.Number || spec == SpecTypeId.Currency);
            switch (value.Type)
            {
                case JTokenType.String:
                    return new ScheduleFilter(field.FieldId, type, value.Value<string>());
                case JTokenType.Integer:
                    return isDouble
                        ? new ScheduleFilter(field.FieldId, type, ToInternal(value.Value<double>(), spec))
                        : new ScheduleFilter(field.FieldId, type, value.Value<int>());
                case JTokenType.Float:
                    return new ScheduleFilter(field.FieldId, type, ToInternal(value.Value<double>(), spec));
                case JTokenType.Boolean:
                    return new ScheduleFilter(field.FieldId, type, value.Value<bool>() ? 1 : 0);
                default:
                    throw new ArgumentException("the filter value on '" + field.GetName() + "' must be a string, number or boolean.");
            }
        }

        /// <summary>For a field whose parameter stores an element id, the id the value names; null for other fields.</summary>
        private static ElementId ResolveElementIdValue(Document doc, ViewSchedule schedule, ScheduleField field, JToken value)
        {
            if (field.ParameterId == ElementId.InvalidElementId || schedule.Definition.CategoryId == ElementId.InvalidElementId) return null;
            var parameterId = RevitCompat.GetId(field.ParameterId);
            var parameterElement = doc.GetElement(field.ParameterId) as ParameterElement;
            Parameter sample = null;
            foreach (var element in new FilteredElementCollector(doc).OfCategoryId(schedule.Definition.CategoryId).WhereElementIsNotElementType())
            {
                var owner = field.FieldType == ScheduleFieldType.ElementType ? doc.GetElement(element.GetTypeId()) : element;
                if (owner == null) continue;
                sample = parameterId < 0
                    ? owner.get_Parameter((BuiltInParameter)(int)parameterId)
                    : parameterElement == null ? null : owner.get_Parameter(parameterElement.GetDefinition());
                if (sample != null) break;
            }
            if (sample == null || sample.StorageType != StorageType.ElementId) return null;

            var reference = doc.GetElement(sample.AsElementId());
            var text = value.Type == JTokenType.String ? value.Value<string>() : value.ToString();
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                var resolved = doc.GetElement(RevitCompat.ToElementId(id));
                if (resolved == null || (reference != null && !reference.GetType().IsInstanceOfType(resolved)))
                    throw new ArgumentException("element " + id + " is not a " + (reference?.GetType().Name ?? "valid value") + " for '" + field.GetName() + "'.");
                return resolved.Id;
            }
            if (reference == null)
                throw new ArgumentException("names cannot be resolved for '" + field.GetName() + "' (no element holds a value yet); pass an element id.");
            var candidates = reference is Phase || reference.Category == null
                ? new FilteredElementCollector(doc).OfClass(reference.GetType())
                : new FilteredElementCollector(doc).OfCategoryId(reference.Category.Id);
            var matches = candidates.Where(e => reference.GetType().IsInstanceOfType(e)
                && string.Equals(e.Name, text, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (matches.Count != 1)
                throw new ArgumentException("element name '" + text + "' is " + (matches.Count == 0 ? "not found" : "ambiguous")
                    + " for '" + field.GetName() + "'; pass its element id.");
            return matches[0].Id;
        }

        // ---- combined parameters ----

        /// <summary>Segments [{parameterName | parameterId, prefix, separator, suffix, sampleValue}] as Revit data.</summary>
        public static bool TryCombinedSegments(Document doc, ScheduleDefinition definition, JArray segments, string where,
            out IList<TableCellCombinedParameterData> data, out CommandResult failure)
        {
            data = new List<TableCellCombinedParameterData>();
            failure = null;
            if (segments == null || segments.Count == 0)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: " + where + " must be a non-empty array of segments.");
                return false;
            }
            var schedulable = definition.GetSchedulableFields();
            for (var i = 0; i < segments.Count; i++)
            {
                var at = where + "[" + i + "]";
                if (!(segments[i] is JObject seg))
                {
                    failure = CommandResult.Fail("INVALID_PARAMS: " + at + " must be an object.");
                    return false;
                }
                if (!Keys(seg, at, out failure, "parameterName", "parameterId", "prefix", "separator", "suffix", "sampleValue")) return false;
                foreach (var key in new[] { "parameterName", "prefix", "separator", "suffix", "sampleValue" })
                    if (seg[key] != null && seg[key].Type != JTokenType.String && seg[key].Type != JTokenType.Null)
                    {
                        failure = CommandResult.Fail("INVALID_PARAMS: " + at + "." + key + " must be a string.");
                        return false;
                    }
                if (seg["parameterId"] != null && seg["parameterId"].Type != JTokenType.Integer)
                {
                    failure = CommandResult.Fail("INVALID_PARAMS: " + at + ".parameterId must be an integer.");
                    return false;
                }

                SchedulableField match;
                var pid = seg.Value<long?>("parameterId");
                var pname = seg.Value<string>("parameterName");
                if (pid.HasValue)
                    match = schedulable.FirstOrDefault(sf => RevitCompat.GetId(sf.ParameterId) == pid.Value);
                else if (!string.IsNullOrWhiteSpace(pname))
                    match = schedulable.FirstOrDefault(sf => string.Equals(sf.GetName(doc), pname, StringComparison.OrdinalIgnoreCase)
                        && sf.ParameterId != ElementId.InvalidElementId);
                else
                {
                    failure = CommandResult.Fail("INVALID_PARAMS: " + at + " needs parameterName or parameterId.");
                    return false;
                }
                if (match == null || match.ParameterId == ElementId.InvalidElementId)
                {
                    failure = CommandResult.Fail("FIELD_NOT_FOUND: " + at + ": '" + (pname ?? pid.ToString()) + "' is not a schedulable parameter of this schedule.",
                        new JObject { ["valid_fields"] = SchedulableList(doc, schedulable) });
                    return false;
                }

                var item = TableCellCombinedParameterData.Create();
                item.ParamId = match.ParameterId;
                item.CategoryId = definition.CategoryId;
                item.Prefix = seg.Value<string>("prefix") ?? string.Empty;
                item.Separator = seg.Value<string>("separator") ?? string.Empty;
                item.Suffix = seg.Value<string>("suffix") ?? string.Empty;
                if (seg.Value<string>("sampleValue") is string sample) item.SampleValue = sample;
                data.Add(item);
            }
            if (!definition.IsValidCombinedParameters(data))
            {
                failure = CommandResult.Fail("INVALID_COMBINED: Revit does not accept these parameters as a combined field.");
                return false;
            }
            return true;
        }

        public static JArray CombinedSegmentsJson(Document doc, ScheduleField field)
        {
            var list = new JArray();
            foreach (var seg in field.GetCombinedParameters())
                list.Add(new JObject
                {
                    ["parameterName"] = ParameterName(doc, seg.ParamId),
                    ["parameterId"] = RevitCompat.GetIdOrNull(seg.ParamId),
                    ["prefix"] = seg.Prefix,
                    ["separator"] = seg.Separator,
                    ["suffix"] = seg.Suffix,
                    ["sampleValue"] = seg.SampleValue
                });
            return list;
        }

        public static JArray SchedulableList(Document doc, IEnumerable<SchedulableField> all) =>
            new JArray(all.Select(sf => sf.GetName(doc)).Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(150));

        // ---- misc ----

        public static bool Keys(JObject entry, string where, out CommandResult failure, params string[] allowed)
        {
            var unknown = entry.Properties().Select(x => x.Name).Where(n => !allowed.Contains(n)).ToList();
            failure = unknown.Count == 0 ? null
                : CommandResult.Fail("INVALID_PARAMS: unknown key(s) " + string.Join(", ", unknown) + " in " + where
                    + ". Valid keys: " + string.Join(", ", allowed) + ".");
            return failure == null;
        }

        /// <summary>The field's kind as get_schedule_definition reports it.</summary>
        public static string Kind(ScheduleField field)
        {
            if (field.FieldType == ScheduleFieldType.Formula) return "formula";
            if (field.FieldType == ScheduleFieldType.Percentage) return "percentage";
            if (field.IsCombinedParameterField) return "combined";
            if (field.FieldType == ScheduleFieldType.Count) return "count";
            return "parameter";
        }
    }
}
