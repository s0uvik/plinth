// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Schedules/GetScheduleDefinitionHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: field kinds come from ScheduleField.FieldType (rvt-mcp reflected for a HasFormula property
// that the 2024-2027 API does not have, so formula fields were reported as parameter fields); formula text is
// not readable through the Revit API and is not reported; columnWidth is the sheet column width in mm
// (rvt-mcp reflected for a nonexistent ColumnWidth and always returned 0); isTotal is derived from
// DisplayType; percentageOf names the field a percentage field is based on; builtInParameter and
// builtInCategory names are added; filter values convert to mm/m2/m3/degrees through ScheduleHelpers;
// the per-member try/catch is reduced to one guard per field, filter and sort entry.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Schedule
{
    public sealed class GetScheduleDefinitionHandler : IRevitCommand
    {
        public string Name => "get_schedule_definition";
        public string Description =>
            "Read a schedule's structure (scheduleId or scheduleName): fields in order (fieldIndex, name, heading, kind parameter/formula/"
            + "percentage/combined/count, parameter, hidden, sheet column width mm, display type, alignment), filters (values in mm/m2/m3/"
            + "degrees), sort/group entries and settings. Formula text is not exposed by the Revit API.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + ScheduleHelpers.ScheduleRefSchema + "}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!ScheduleHelpers.TryResolve(doc, p, out var schedule, out var failure)) return failure;
            var definition = schedule.Definition;

            var order = definition.GetFieldOrder();
            var fields = new JArray();
            for (var i = 0; i < order.Count; i++) fields.Add(Field(doc, definition, order[i], i));

            var filters = new JArray();
            for (var i = 0; i < definition.GetFilterCount(); i++) filters.Add(Filter(definition, i));

            var sorts = new JArray();
            for (var i = 0; i < definition.GetSortGroupFieldCount(); i++)
            {
                try
                {
                    var s = definition.GetSortGroupField(i);
                    sorts.Add(new JObject
                    {
                        ["fieldIndex"] = ScheduleHelpers.IndexOf(definition, s.FieldId),
                        ["fieldName"] = definition.GetField(s.FieldId)?.GetName(),
                        ["sortOrder"] = s.SortOrder.ToString(),
                        ["showHeader"] = s.ShowHeader,
                        ["showFooter"] = s.ShowFooter,
                        ["showBlankLine"] = s.ShowBlankLine
                    });
                }
                catch (Exception ex)
                {
                    sorts.Add(new JObject { ["error"] = ex.Message });
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["id"] = RevitCompat.GetId(schedule.Id),
                ["name"] = schedule.Name,
                ["categoryName"] = ScheduleHelpers.CategoryName(doc, definition),
                ["builtInCategory"] = ScheduleHelpers.BuiltInCategoryName(definition),
                ["isKey"] = definition.IsKeySchedule,
                ["isItemized"] = definition.IsItemized,
                ["showGrandTotal"] = definition.ShowGrandTotal,
                ["showHeaders"] = definition.ShowHeaders,
                ["showTitle"] = definition.ShowTitle,
                ["fields"] = fields,
                ["filters"] = filters,
                ["sortGroup"] = sorts
            });
        }

        private static JObject Field(Document doc, ScheduleDefinition definition, ScheduleFieldId id, int index)
        {
            try
            {
                var field = definition.GetField(id);
                var kind = ScheduleHelpers.Kind(field);
                var hasParameter = field.ParameterId != null && field.ParameterId != ElementId.InvalidElementId;
                var dto = new JObject
                {
                    ["fieldIndex"] = index,
                    ["name"] = field.GetName(),
                    ["columnHeading"] = field.ColumnHeading,
                    ["type"] = kind,
                    ["fieldType"] = field.FieldType.ToString(),
                    ["parameterName"] = hasParameter ? ScheduleHelpers.ParameterName(doc, field.ParameterId) : null,
                    ["parameterId"] = hasParameter ? RevitCompat.GetId(field.ParameterId) : (long?)null,
                    ["builtInParameter"] = ScheduleHelpers.BuiltInParameterName(field.ParameterId),
                    ["specTypeId"] = ScheduleHelpers.SpecOf(field)?.TypeId,
                    ["hidden"] = field.IsHidden,
                    ["columnWidth"] = Mm.FromFeet(field.SheetColumnWidth),
                    ["isTotal"] = field.DisplayType == ScheduleFieldDisplayType.Totals,
                    ["displayType"] = field.DisplayType.ToString(),
                    ["horizontalAlignment"] = field.HorizontalAlignment.ToString(),
                    ["headingOrientation"] = field.HeadingOrientation.ToString()
                };
                if (kind == "combined") dto["combinedSegments"] = ScheduleHelpers.CombinedSegmentsJson(doc, field);
                if (kind == "percentage")
                {
                    var of = field.PercentageOf;
                    dto["percentageOf"] = of != null && definition.IsValidFieldId(of) ? definition.GetField(of).GetName() : null;
                }
                return dto;
            }
            catch (Exception ex)
            {
                return new JObject { ["fieldIndex"] = index, ["error"] = ex.Message };
            }
        }

        private static JObject Filter(ScheduleDefinition definition, int index)
        {
            try
            {
                var f = definition.GetFilter(index);
                var field = definition.GetField(f.FieldId);
                var spec = ScheduleHelpers.SpecOf(field);
                JToken value = JValue.CreateNull();
                if (f.IsStringValue) value = f.GetStringValue();
                else if (f.IsDoubleValue) value = Math.Round(ScheduleHelpers.ToDisplay(f.GetDoubleValue(), spec), 6);
                else if (f.IsIntegerValue) value = f.GetIntegerValue();
                else if (f.IsElementIdValue) value = RevitCompat.GetId(f.GetElementIdValue());
                return new JObject
                {
                    ["fieldIndex"] = ScheduleHelpers.IndexOf(definition, f.FieldId),
                    ["fieldName"] = field.GetName(),
                    ["operator"] = f.FilterType.ToString(),
                    ["value"] = value,
                    ["valueUnits"] = f.IsDoubleValue ? ScheduleHelpers.UnitLabel(spec) : null
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["filterIndex"] = index, ["error"] = ex.Message };
            }
        }
    }
}
