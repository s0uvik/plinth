// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Schedules/GetScheduleFormulasHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: calculated fields are found through ScheduleField.FieldType (Formula, Percentage); rvt-mcp
// reflected for HasFormula/Formula/GetFormula members that the 2024-2027 API does not have, so it never
// reported a formula field. The Revit API does not expose formula text, so formula is always null and the
// dependency parsing that read it is dropped; percentage fields report the field they are a percentage of
// (PercentageOf) and the field they are grouped by (PercentageBy). Combined fields read their segments through
// the public TableCellCombinedParameterData API instead of reflection.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Schedule
{
    public sealed class GetScheduleFormulasHandler : IRevitCommand
    {
        public string Name => "get_schedule_formulas";
        public string Description =>
            "List a schedule's calculated fields (formula and percentage) and combined-parameter fields with their segments "
            + "(scheduleId or scheduleName). The Revit API does not expose formula text: formula fields report name, heading and unit type only.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + ScheduleHelpers.ScheduleRefSchema + "}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!ScheduleHelpers.TryResolve(doc, p, out var schedule, out var failure)) return failure;
            var definition = schedule.Definition;

            var items = new JArray();
            int formulas = 0, percentages = 0, combined = 0;
            var order = definition.GetFieldOrder();
            for (var i = 0; i < order.Count; i++)
            {
                ScheduleField field;
                try { field = definition.GetField(order[i]); }
                catch (Exception) { continue; }
                var kind = ScheduleHelpers.Kind(field);
                if (kind != "formula" && kind != "percentage" && kind != "combined") continue;

                var item = new JObject
                {
                    ["fieldIndex"] = i,
                    ["name"] = field.GetName(),
                    ["columnHeading"] = field.ColumnHeading,
                    ["kind"] = kind,
                    ["specTypeId"] = ScheduleHelpers.SpecOf(field)?.TypeId
                };
                if (kind == "formula")
                {
                    formulas++;
                    item["formula"] = null;
                }
                else if (kind == "percentage")
                {
                    percentages++;
                    item["percentageOfFieldName"] = FieldName(definition, field.PercentageOf);
                    item["percentageByFieldName"] = FieldName(definition, field.PercentageBy);
                }
                else
                {
                    combined++;
                    try { item["combinedSegments"] = ScheduleHelpers.CombinedSegmentsJson(doc, field); }
                    catch (Exception ex) { item["error"] = ex.Message; }
                }
                items.Add(item);
            }

            return CommandResult.Ok(new JObject
            {
                ["scheduleId"] = RevitCompat.GetId(schedule.Id),
                ["scheduleName"] = schedule.Name,
                ["totalFormulaFields"] = formulas,
                ["totalPercentageFields"] = percentages,
                ["totalCombinedFields"] = combined,
                ["formulaTextAvailable"] = false,
                ["formulas"] = items
            });
        }

        private static string FieldName(ScheduleDefinition definition, ScheduleFieldId id)
        {
            try { return id != null && definition.IsValidFieldId(id) ? definition.GetField(id).GetName() : null; }
            catch (Exception) { return null; }
        }
    }
}
