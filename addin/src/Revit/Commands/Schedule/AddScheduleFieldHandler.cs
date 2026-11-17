// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Schedules/AddScheduleFieldHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: field.type "formula" is refused with NOT_SUPPORTED: the Revit 2024-2027 API has no way to
// create a formula field or set its formula (rvt-mcp reflected for AddField(type, name, spec) and SetFormula,
// which do not exist, so it always failed). Combined fields use ScheduleDefinition.InsertCombinedParameterField
// with the given name (rvt-mcp added a text "anchor" field and set combined parameters on it by reflection).
// A parameter name is matched by field name or BuiltInParameter name; a name shared by several field types
// without fieldType is an error listing them (rvt-mcp took the first). insertIndex outside 0..fieldCount is an
// error (rvt-mcp clamped it). columnWidth sets the sheet column width in mm (rvt-mcp's reflected ColumnWidth
// setter did nothing). columnHeading/hidden failures are errors instead of being ignored; nested keys are checked.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Schedule
{
    public sealed class AddScheduleFieldHandler : IRevitCommand
    {
        public string Name => "add_schedule_field";
        public string Description =>
            "Add one field to a schedule (scheduleId or scheduleName). field: {type: parameter, parameterName (field or BuiltInParameter "
            + "name, see get_schedulable_fields), fieldType (Instance, ElementType, ... when the name repeats)} or {type: combined, name, "
            + "segments: [{parameterName | parameterId, prefix, separator, suffix, sampleValue}]}. Formula fields cannot be created "
            + "through the Revit API. Optional insertIndex (0 = first column, default last), columnHeading, hidden, columnWidth (mm).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + ScheduleHelpers.ScheduleRefSchema + @",
            ""field"":{""type"":""object""},
            ""insertIndex"":{""type"":""integer""},
            ""columnHeading"":{""type"":""string""},
            ""hidden"":{""type"":""boolean""},
            ""columnWidth"":{""type"":""number""}},
            ""required"":[""field""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var spec = (JObject)p["field"];
            var kind = (spec.Value<string>("type") ?? "").Trim().ToLowerInvariant();
            CommandResult failure;
            switch (kind)
            {
                case "parameter":
                    if (!ScheduleHelpers.Keys(spec, "field", out failure, "type", "parameterName", "fieldType")) return failure;
                    if (string.IsNullOrWhiteSpace(spec.Value<string>("parameterName")))
                        return CommandResult.Fail("INVALID_PARAMS: field.parameterName is required for type parameter.");
                    break;
                case "combined":
                    if (!ScheduleHelpers.Keys(spec, "field", out failure, "type", "name", "segments")) return failure;
                    if (string.IsNullOrWhiteSpace(spec.Value<string>("name")))
                        return CommandResult.Fail("INVALID_PARAMS: field.name is required for type combined.");
                    if (!(spec["segments"] is JArray))
                        return CommandResult.Fail("INVALID_PARAMS: field.segments must be an array for type combined.");
                    break;
                case "formula":
                    return CommandResult.Fail("NOT_SUPPORTED: the Revit API (2024-2027) cannot create formula fields or set a formula. "
                        + "Add the formula in Revit's schedule properties.");
                default:
                    return CommandResult.Fail("INVALID_PARAMS: field.type must be parameter or combined.");
            }

            var widthMm = p.Value<double?>("columnWidth");
            if (widthMm.HasValue && widthMm.Value <= 0) return CommandResult.Fail("INVALID_PARAMS: columnWidth must be greater than 0 mm.");
            if (!ScheduleHelpers.TryResolve(doc, p, out var schedule, out failure)) return failure;
            var definition = schedule.Definition;
            var count = definition.GetFieldCount();
            var insertIndex = p.Value<int?>("insertIndex");
            if (insertIndex.HasValue && (insertIndex.Value < 0 || insertIndex.Value > count))
                return CommandResult.Fail("INVALID_PARAMS: insertIndex must be 0 to " + count + " (the schedule has " + count + " fields).");
            var index = insertIndex ?? count;

            // Resolve everything before the transaction.
            SchedulableField match = null;
            IList<TableCellCombinedParameterData> segments = null;
            if (kind == "parameter")
            {
                if (!TryParameter(doc, definition, spec.Value<string>("parameterName"), spec.Value<string>("fieldType"), out match, out failure))
                    return failure;
            }
            else if (!ScheduleHelpers.TryCombinedSegments(doc, definition, (JArray)spec["segments"], "field.segments", out segments, out failure))
            {
                return failure;
            }

            using (var tx = new Transaction(doc, "Plinth: Add Schedule Field"))
            {
                tx.Start();
                try
                {
                    var field = kind == "parameter"
                        ? definition.InsertField(match, index)
                        : definition.InsertCombinedParameterField(segments, spec.Value<string>("name"), index);
                    if (field == null) throw new InvalidOperationException("Revit returned no field.");

                    var heading = p.Value<string>("columnHeading");
                    if (!string.IsNullOrEmpty(heading)) field.ColumnHeading = heading;
                    if (p.Value<bool?>("hidden") is bool hidden) field.IsHidden = hidden;
                    if (widthMm.HasValue) field.SheetColumnWidth = Mm.ToFeet(widthMm.Value);

                    var fieldIndex = ScheduleHelpers.IndexOf(definition, field.FieldId);
                    var name = field.GetName();
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    return CommandResult.Ok(new JObject
                    {
                        ["scheduleId"] = RevitCompat.GetId(schedule.Id),
                        ["scheduleName"] = schedule.Name,
                        ["fieldIndex"] = fieldIndex,
                        ["fieldName"] = name,
                        ["columnHeading"] = field.ColumnHeading,
                        ["type"] = kind,
                        ["fieldType"] = field.FieldType.ToString(),
                        ["hidden"] = field.IsHidden,
                        ["columnWidth"] = Mm.FromFeet(field.SheetColumnWidth)
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("ADD_FAILED: could not add the field: " + ex.Message + " Nothing was changed.");
                }
            }
        }

        private static bool TryParameter(Document doc, ScheduleDefinition definition, string name, string fieldType,
            out SchedulableField match, out CommandResult failure)
        {
            match = null;
            failure = null;
            ScheduleFieldType? wanted = null;
            if (!string.IsNullOrEmpty(fieldType))
            {
                if (!Enum.TryParse(fieldType, true, out ScheduleFieldType parsed) || !Enum.IsDefined(typeof(ScheduleFieldType), parsed))
                {
                    failure = CommandResult.Fail("INVALID_PARAMS: field.fieldType '" + fieldType + "' is not a field type.",
                        new JObject { ["valid_field_types"] = new JArray(Enum.GetNames(typeof(ScheduleFieldType))) });
                    return false;
                }
                wanted = parsed;
            }

            var all = definition.GetSchedulableFields();
            ElementId bipId = null;
            if (char.IsLetter(name[0]) && Enum.TryParse(name, false, out BuiltInParameter bip) && Enum.IsDefined(typeof(BuiltInParameter), bip))
                bipId = new ElementId(bip);
            var matches = all.Where(sf => (bipId != null ? sf.ParameterId == bipId : string.Equals(sf.GetName(doc), name, StringComparison.OrdinalIgnoreCase))
                && (wanted == null || sf.FieldType == wanted.Value)).ToList();
            if (matches.Count == 1)
            {
                match = matches[0];
                return true;
            }
            failure = matches.Count == 0
                ? CommandResult.Fail("FIELD_NOT_FOUND: this schedule cannot show '" + name + "'"
                    + (wanted == null ? "" : " as a " + wanted.Value + " field") + ".",
                    new JObject { ["valid_fields"] = ScheduleHelpers.SchedulableList(doc, all) })
                : CommandResult.Fail("FIELD_AMBIGUOUS: '" + name + "' exists as several field types; pass field.fieldType.",
                    new JObject { ["field_types"] = new JArray(matches.Select(m => m.FieldType.ToString()).Distinct()) });
            return false;
        }
    }
}
