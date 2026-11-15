// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Schedules/UpdateScheduleFieldHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: every change is checked before the transaction and all of them apply or none do (rvt-mcp
// skipped bad or unknown keys and committed the rest); "formula" and "isPercentage" are refused with
// NOT_SUPPORTED because the Revit 2024-2027 API cannot read or write them (rvt-mcp reflected for members that
// do not exist); isTotal maps to DisplayType Totals/Standard and checks CanTotal (rvt-mcp reflected for a
// nonexistent IsTotal); columnWidth sets the sheet column width in mm (rvt-mcp's ColumnWidth setter did
// nothing); combinedParameters use the public TableCellCombinedParameterData API; a field reference miss
// lists the schedule's fields.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Schedule
{
    public sealed class UpdateScheduleFieldHandler : IRevitCommand
    {
        private static readonly string[] ChangeKeys =
            { "columnHeading", "hidden", "columnWidth", "horizontalAlignment", "headingOrientation", "combinedParameters", "isTotal", "displayType" };

        public string Name => "update_schedule_field";
        public string Description =>
            "Change one field of a schedule (scheduleId or scheduleName). fieldRef: {fieldIndex (0-based, as get_schedule_definition) "
            + "or fieldName (name or heading)}. changes: columnHeading, hidden, columnWidth (mm), horizontalAlignment (Left/Center/Right), "
            + "headingOrientation (Horizontal/Vertical), isTotal, displayType (Standard/Totals/Min/Max/MinMax), combinedParameters "
            + "(combined fields: [{parameterName | parameterId, prefix, separator, suffix, sampleValue}]). All changes apply or none. "
            + "A field's parameter cannot be changed: remove it and add another.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + ScheduleHelpers.ScheduleRefSchema + @",
            ""fieldRef"":{""type"":""object""},
            ""changes"":{""type"":""object""}},
            ""required"":[""fieldRef"",""changes""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var fieldRef = (JObject)p["fieldRef"];
            var changes = (JObject)p["changes"];
            if (!ScheduleHelpers.Keys(fieldRef, "fieldRef", out var failure, "fieldIndex", "fieldId", "fieldName")) return failure;
            var indexToken = fieldRef["fieldIndex"] ?? fieldRef["fieldId"];
            if (indexToken != null && indexToken.Type != JTokenType.Integer)
                return CommandResult.Fail("INVALID_PARAMS: fieldRef.fieldIndex must be an integer.");
            if (changes["parameterName"] != null || changes["parameterId"] != null)
                return CommandResult.Fail("INVALID_PARAMS: a field's parameter cannot be changed; remove the field and add one with the new parameter.");
            if (changes["formula"] != null || changes["isPercentage"] != null)
                return CommandResult.Fail("NOT_SUPPORTED: the Revit API (2024-2027) cannot set a schedule formula or percentage; change it in Revit.");
            if (!ScheduleHelpers.Keys(changes, "changes", out failure, ChangeKeys)) return failure;
            if (!changes.Properties().Any()) return CommandResult.Fail("INVALID_PARAMS: changes is empty.");

            if (!ScheduleHelpers.TryResolve(doc, p, out var schedule, out failure)) return failure;
            var definition = schedule.Definition;
            if (!ScheduleHelpers.TryField(definition, indexToken?.Value<int>(), fieldRef.Value<string>("fieldName"),
                    out var field, out var fieldIndex, out failure))
                return failure;

            // Check and parse every change first.
            string heading = null;
            bool? hidden = null, isTotal = null;
            double? widthMm = null;
            ScheduleHorizontalAlignment? align = null;
            ScheduleHeadingOrientation? orientation = null;
            ScheduleFieldDisplayType? display = null;
            IList<TableCellCombinedParameterData> segments = null;
            foreach (var change in changes.Properties())
            {
                var v = change.Value;
                switch (change.Name)
                {
                    case "columnHeading":
                        if (v.Type != JTokenType.String && v.Type != JTokenType.Null) return Bad("columnHeading must be a string.");
                        heading = v.Type == JTokenType.Null ? string.Empty : v.Value<string>();
                        break;
                    case "hidden":
                        if (v.Type != JTokenType.Boolean) return Bad("hidden must be true or false.");
                        hidden = v.Value<bool>();
                        break;
                    case "columnWidth":
                        if ((v.Type != JTokenType.Integer && v.Type != JTokenType.Float) || v.Value<double>() <= 0)
                            return Bad("columnWidth must be a number of mm greater than 0.");
                        widthMm = v.Value<double>();
                        break;
                    case "horizontalAlignment":
                        if (!TryEnum(v, out ScheduleHorizontalAlignment a)) return Bad("horizontalAlignment must be Left, Center or Right.");
                        align = a;
                        break;
                    case "headingOrientation":
                        if (!TryEnum(v, out ScheduleHeadingOrientation o)) return Bad("headingOrientation must be Horizontal or Vertical.");
                        orientation = o;
                        break;
                    case "displayType":
                        if (!TryEnum(v, out ScheduleFieldDisplayType d)) return Bad("displayType must be Standard, Totals, Min, Max or MinMax.");
                        display = d;
                        break;
                    case "isTotal":
                        if (v.Type != JTokenType.Boolean) return Bad("isTotal must be true or false.");
                        isTotal = v.Value<bool>();
                        break;
                    case "combinedParameters":
                        if (!field.IsCombinedParameterField)
                            return CommandResult.Fail("NOT_COMBINED: field '" + field.GetName() + "' is not a combined-parameter field.");
                        if (!ScheduleHelpers.TryCombinedSegments(doc, definition, v as JArray, "changes.combinedParameters", out segments, out failure))
                            return failure;
                        break;
                }
            }
            if (isTotal.HasValue && display.HasValue)
                return CommandResult.Fail("INVALID_PARAMS: pass changes.isTotal or changes.displayType, not both.");
            if (isTotal == true) display = ScheduleFieldDisplayType.Totals;
            else if (isTotal == false) display = field.DisplayType == ScheduleFieldDisplayType.Totals ? ScheduleFieldDisplayType.Standard : field.DisplayType;
            if (display == ScheduleFieldDisplayType.Totals && !field.CanTotal())
                return CommandResult.Fail("CANNOT_TOTAL: field '" + field.GetName() + "' cannot be totalled (it is not numeric).");
            if ((display == ScheduleFieldDisplayType.Min || display == ScheduleFieldDisplayType.Max || display == ScheduleFieldDisplayType.MinMax)
                && !field.CanDisplayMinMax())
                return CommandResult.Fail("CANNOT_MINMAX: field '" + field.GetName() + "' cannot show minimum/maximum values.");

            using (var tx = new Transaction(doc, "B2M: Update Schedule Field"))
            {
                tx.Start();
                try
                {
                    if (heading != null) field.ColumnHeading = heading;
                    if (hidden.HasValue) field.IsHidden = hidden.Value;
                    if (widthMm.HasValue) field.SheetColumnWidth = Mm.ToFeet(widthMm.Value);
                    if (align.HasValue) field.HorizontalAlignment = align.Value;
                    if (orientation.HasValue) field.HeadingOrientation = orientation.Value;
                    if (display.HasValue) field.DisplayType = display.Value;
                    if (segments != null) field.SetCombinedParameters(segments);

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("UPDATE_FAILED: could not update field '" + field.GetName() + "': " + ex.Message + " Nothing was changed.");
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["scheduleId"] = RevitCompat.GetId(schedule.Id),
                ["scheduleName"] = schedule.Name,
                ["fieldIndex"] = fieldIndex,
                ["fieldName"] = field.GetName(),
                ["applied"] = new JArray(changes.Properties().Select(c => c.Name)),
                ["columnHeading"] = field.ColumnHeading,
                ["hidden"] = field.IsHidden,
                ["columnWidth"] = Mm.FromFeet(field.SheetColumnWidth),
                ["displayType"] = field.DisplayType.ToString()
            });
        }

        private static CommandResult Bad(string message) => CommandResult.Fail("INVALID_PARAMS: changes." + message);

        private static bool TryEnum<T>(JToken token, out T value) where T : struct
        {
            value = default(T);
            return token.Type == JTokenType.String && Enum.TryParse(token.Value<string>().Trim(), true, out value)
                && Enum.IsDefined(typeof(T), value);
        }
    }
}
