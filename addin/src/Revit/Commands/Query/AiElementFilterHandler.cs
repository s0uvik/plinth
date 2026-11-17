// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Elements/AiElementFilterHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: category accepts OST_ names as well as display names and a miss lists the valid ones; numbers
// compare in wire units (length mm, area m2, volume m3, angle degrees; rvt-mcp used mm2/mm3 via literal factors);
// numeric equals has a unit-sized tolerance; parameterName without parameterValue now fails instead of being ignored;
// an unparsable number for greaterthan/lessthan fails instead of matching nothing; numbers parse invariantly.

using System;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Query
{
    public sealed class AiElementFilterHandler : IRevitCommand
    {
        public string Name => "ai_element_filter";
        public string Description =>
            "Find elements of a category (OST_Walls or display name), optionally where a parameter equals / contains / startswith / "
            + "greaterthan / lessthan a value. Numbers are in mm, m2, m3 or degrees. select: true also selects the matches in Revit.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""category"":{""type"":""string""},
            ""parameterName"":{""type"":""string""},
            ""parameterValue"":{""type"":""string""},
            ""operator"":{""type"":""string"",""enum"":[""equals"",""contains"",""startswith"",""greaterthan"",""lessthan""]},
            ""limit"":{""type"":""integer""},
            ""select"":{""type"":""boolean""}},""required"":[""category""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var paramName = p.Value<string>("parameterName");
            var paramValue = p.Value<string>("parameterValue");
            var op = p.Value<string>("operator") ?? "equals";
            var limit = p.Value<int?>("limit") ?? 100;
            if (limit < 1 || limit > 1000) return CommandResult.Fail("INVALID_PARAMS: limit must be between 1 and 1000.");
            if (string.IsNullOrEmpty(paramName) != (paramValue == null))
                return CommandResult.Fail("INVALID_PARAMS: parameterName and parameterValue go together.");
            var numericOp = op == "greaterthan" || op == "lessthan";
            double target = 0;
            var isNumber = paramValue != null && double.TryParse(paramValue, NumberStyles.Float, CultureInfo.InvariantCulture, out target);
            if (numericOp && !isNumber) return CommandResult.Fail("INVALID_PARAMS: " + op + " needs a number in parameterValue.");
            if (!QueryHelpers.TryCategory(doc, p.Value<string>("category"), out var category, out var failure)) return failure;

            var elements = new FilteredElementCollector(doc).OfCategoryId(category.Id).WhereElementIsNotElementType().ToList();
            if (!string.IsNullOrEmpty(paramName))
                elements = elements.Where(e => Matches(doc, e.LookupParameter(paramName), op, paramValue, isNumber, target)).ToList();

            var matched = elements.Take(limit).ToList();
            if ((p.Value<bool?>("select") ?? false) && uidoc != null)
                uidoc.Selection.SetElementIds(matched.Select(e => e.Id).ToList());

            return CommandResult.Ok(new JObject
            {
                ["category"] = category.Name,
                ["totalFound"] = elements.Count,
                ["returned"] = matched.Count,
                ["truncated"] = elements.Count > matched.Count,
                ["selected"] = p.Value<bool?>("select") ?? false,
                ["elements"] = new JArray(matched.Select(e =>
                {
                    var row = new JObject
                    {
                        ["elementId"] = RevitCompat.GetId(e.Id),
                        ["name"] = QueryHelpers.NameOf(e),
                        ["category"] = e.Category?.Name,
                        ["typeName"] = QueryHelpers.NameOf(doc.GetElement(e.GetTypeId()))
                    };
                    if (!string.IsNullOrEmpty(paramName) && e.LookupParameter(paramName) is Parameter found)
                        row["parameterValue"] = QueryHelpers.Value(found);
                    return row;
                }))
            });
        }

        private static bool Matches(Document doc, Parameter parameter, string op, string value, bool isNumber, double target)
        {
            if (parameter == null) return false;
            var textOp = op == "contains" || op == "startswith";
            if (!textOp && (parameter.StorageType == StorageType.Double || (parameter.StorageType == StorageType.Integer && isNumber)))
            {
                if (!isNumber) return false;
                double actual;
                double tolerance;
                if (parameter.StorageType == StorageType.Double)
                {
                    var unit = QueryHelpers.WireUnit(parameter);
                    actual = QueryHelpers.ToWire(parameter.AsDouble(), unit);
                    tolerance = unit == "mm" ? 0.5 : 1e-6;
                }
                else
                {
                    actual = parameter.AsInteger();
                    tolerance = 0;
                }
                switch (op)
                {
                    case "greaterthan": return actual > target;
                    case "lessthan": return actual < target;
                    case "equals": return Math.Abs(actual - target) <= tolerance;
                }
            }
            if (op == "greaterthan" || op == "lessthan") return false;
            var text = QueryHelpers.Text(doc, parameter);
            if (text == null) return false;
            switch (op)
            {
                case "contains": return text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
                case "startswith": return text.StartsWith(value, StringComparison.OrdinalIgnoreCase);
                default: return string.Equals(text, value, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
