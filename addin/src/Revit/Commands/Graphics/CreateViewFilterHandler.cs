// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Graphics/CreateViewFilterHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: Plinth supports Revit 2024 and later, so rule factories are called directly instead of probed
// by reflection; rule parameters resolve against the parameters Revit can filter on for all the chosen
// categories (by built-in name such as WALL_BASE_OFFSET, label or shared/project name), with the value type
// taken from the parameter's storage instead of guessed; every rule must build or nothing is created (rvt-mcp
// dropped rules it could not build and created the filter anyway); unknown or unfilterable categories are errors
// listing the filterable ones (rvt-mcp ignored them); duplicate names fail before the transaction; numeric values
// are mm, m2, m3 or degrees, other units the project's display unit, reported per rule; not_* variants,
// greater_or_equal, less_or_equal, has_value and has_no_value added.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Graphics
{
    public sealed class CreateViewFilterHandler : IRevitCommand
    {
        private const int MaxRules = 50;
        private const double Epsilon = 1e-4; // Internal units; 0.03 mm for lengths.

        private static readonly string[] StringOnly = { "contains", "not_contains", "begins_with", "not_begins_with", "ends_with", "not_ends_with" };
        private static readonly string[] Comparisons = { "equals", "not_equals", "greater", "greater_or_equal", "less", "less_or_equal" };
        private static readonly string[] ValueFree = { "has_value", "has_no_value" };
        private static readonly HashSet<string> RuleKeys = new HashSet<string> { "parameter_name", "evaluator", "value" };

        public string Name => "create_view_filter";
        public string Description => "Create a parameter view filter for categories (built-in names such as OST_Walls, or names shown in Revit). "
            + "Optional rules, all of which must match: {parameter_name, evaluator, value}; evaluator is equals, not_equals, greater, "
            + "greater_or_equal, less, less_or_equal, contains, not_contains, begins_with, not_begins_with, ends_with, not_ends_with, "
            + "has_value or has_no_value. Lengths in mm, areas m2, volumes m3, angles degrees. Without rules the filter matches every element of the categories.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""name"":{""type"":""string""},
            ""categories"":{""type"":""array"",""items"":{""type"":""string""}},
            ""rules"":{""type"":""array"",""items"":{""type"":""object""}}},""required"":[""name"",""categories""]}";
        public bool ReadOnly => false;

        private sealed class ParamInfo
        {
            public ElementId Id;
            public string Name;
            public string BuiltIn;
        }

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var name = p.Value<string>("name")?.Trim();
            if (string.IsNullOrEmpty(name)) return CommandResult.Fail("INVALID_PARAMS: name must not be empty.");
            if (!FilterElement.IsNameUnique(doc, name))
            {
                var existing = new FilteredElementCollector(doc).OfClass(typeof(FilterElement)).Cast<FilterElement>()
                    .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                return CommandResult.Fail("NAME_IN_USE: a filter named '" + name + "' already exists.",
                    new JObject { ["existing_filter_id"] = existing == null ? null : new JValue(RevitCompat.GetId(existing.Id)) });
            }

            var filterable = ParameterFilterUtilities.GetAllFilterableCategories();
            var filterableSet = new HashSet<long>(filterable.Select(RevitCompat.GetId));
            var options = filterable.Select(id => Category.GetCategory(doc, id)).Where(c => c != null).ToList();
            if (!GraphicsHelpers.TryCategories(doc, p["categories"].Select(t => t.Value<string>()),
                    c => filterableSet.Contains(RevitCompat.GetId(c.Id)), "cannot be used in view filters", options,
                    out var categories, out var failure)) return failure;
            var categoryIds = categories.Select(c => c.Id).ToList();

            var ruleSpecs = p["rules"] as JArray ?? new JArray();
            if (ruleSpecs.Count > MaxRules) return CommandResult.Fail("INVALID_PARAMS: at most " + MaxRules + " rules.");

            var rules = new List<FilterRule>();
            var ruleReport = new JArray();
            if (ruleSpecs.Count > 0)
            {
                var parameters = FilterableParameters(doc, categoryIds);
                var errors = new JArray();
                for (var i = 0; i < ruleSpecs.Count; i++)
                {
                    if (TryRule(doc, ruleSpecs[i] as JObject, i, categoryIds, parameters, out var rule, out var report, out var error))
                    {
                        rules.Add(rule);
                        ruleReport.Add(report);
                    }
                    else
                    {
                        errors.Add(error);
                    }
                }
                if (errors.Count > 0)
                {
                    var data = new JObject { ["rule_errors"] = errors };
                    if (errors.Any(e => e.Value<string>("code") == "PARAMETER_NOT_FOUND"))
                        data["valid_parameters"] = new JArray(parameters.OrderBy(x => x.Name).Take(300)
                            .Select(x => new JObject { ["name"] = x.Name, ["built_in"] = x.BuiltIn }));
                    return CommandResult.Fail("RULE_INVALID: " + errors.Count + " of " + ruleSpecs.Count + " rule(s) could not be built; nothing was created.", data);
                }
            }

            ElementParameterFilter elementFilter = null;
            if (rules.Count > 0)
            {
                elementFilter = new ElementParameterFilter(rules);
                if (!ParameterFilterElement.ElementFilterIsAcceptableForParameterFilterElement(doc, new HashSet<ElementId>(categoryIds), elementFilter))
                    return CommandResult.Fail("RULE_INVALID: Revit does not accept these rules for these categories; nothing was created.");
            }

            using (var tx = new Transaction(doc, "Plinth: Create View Filter"))
            {
                tx.Start();
                try
                {
                    var created = elementFilter == null
                        ? ParameterFilterElement.Create(doc, name, categoryIds)
                        : ParameterFilterElement.Create(doc, name, categoryIds, elementFilter);
                    if (tx.Commit() != TransactionStatus.Committed) return GraphicsHelpers.NotCommitted();
                    return CommandResult.Ok(new JObject
                    {
                        ["filter_id"] = RevitCompat.GetId(created.Id),
                        ["name"] = created.Name,
                        ["categories"] = new JArray(categories.Select(GraphicsHelpers.CategoryRef)),
                        ["rule_count"] = rules.Count,
                        ["rules"] = ruleReport
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create view filter '" + name + "': " + ex.Message);
                }
            }
        }

        private static List<ParamInfo> FilterableParameters(Document doc, ICollection<ElementId> categoryIds)
        {
            var result = new List<ParamInfo>();
            foreach (var id in ParameterFilterUtilities.GetFilterableParametersInCommon(doc, categoryIds))
            {
                if (ParameterUtils.IsBuiltInParameter(id))
                {
                    var bip = (BuiltInParameter)(int)RevitCompat.GetId(id);
                    string label;
                    try { label = LabelUtils.GetLabelFor(bip); }
                    catch { label = bip.ToString(); }
                    result.Add(new ParamInfo { Id = id, Name = label, BuiltIn = bip.ToString() });
                }
                else if (doc.GetElement(id) is ParameterElement pe)
                {
                    result.Add(new ParamInfo { Id = id, Name = pe.GetDefinition()?.Name ?? pe.Name });
                }
            }
            return result;
        }

        private static bool TryRule(Document doc, JObject spec, int index, IList<ElementId> categoryIds, List<ParamInfo> parameters,
            out FilterRule rule, out JObject report, out JObject error)
        {
            rule = null;
            report = null;
            error = null;
            JObject Err(string code, string message) => new JObject { ["index"] = index, ["code"] = code, ["error"] = message };

            if (spec == null)
            {
                error = Err("INVALID_RULE", "each rule must be an object {parameter_name, evaluator, value}.");
                return false;
            }
            var extra = spec.Properties().Select(x => x.Name).Where(n => !RuleKeys.Contains(n)).ToList();
            if (extra.Count > 0)
            {
                error = Err("INVALID_RULE", "unknown field(s) " + string.Join(", ", extra) + "; valid: parameter_name, evaluator, value.");
                return false;
            }
            var paramName = (spec["parameter_name"]?.Type == JTokenType.String ? spec.Value<string>("parameter_name") : null)?.Trim();
            var evaluator = (spec["evaluator"]?.Type == JTokenType.String ? spec.Value<string>("evaluator") : null)?.Trim().ToLowerInvariant();
            var value = spec["value"];
            if (string.IsNullOrEmpty(paramName))
            {
                error = Err("INVALID_RULE", "parameter_name must be a non-empty string.");
                return false;
            }
            var allEvaluators = Comparisons.Concat(StringOnly).Concat(ValueFree).ToArray();
            if (evaluator == null || !allEvaluators.Contains(evaluator))
            {
                error = Err("INVALID_RULE", "evaluator must be one of " + string.Join(", ", allEvaluators) + ".");
                return false;
            }

            var matches = parameters.Where(x => x.BuiltIn == paramName).ToList();
            if (matches.Count == 0) matches = parameters.Where(x => string.Equals(x.Name, paramName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                error = Err("PARAMETER_NOT_FOUND", "'" + paramName + "' is not a parameter every chosen category can be filtered on.");
                return false;
            }
            if (matches.Count > 1)
            {
                error = Err("PARAMETER_AMBIGUOUS", "several parameters are named '" + paramName + "'; use the built-in name: "
                    + string.Join(", ", matches.Select(m => m.BuiltIn ?? "(shared/project parameter " + RevitCompat.GetId(m.Id) + ")")) + ".");
                return false;
            }
            var param = matches[0];
            var pid = param.Id;

            report = new JObject { ["index"] = index, ["parameter"] = param.Name, ["built_in"] = param.BuiltIn, ["evaluator"] = evaluator };

            if (ValueFree.Contains(evaluator))
            {
                if (value != null && value.Type != JTokenType.Null)
                {
                    error = Err("INVALID_RULE", evaluator + " takes no value.");
                    return false;
                }
                rule = evaluator == "has_value"
                    ? ParameterFilterRuleFactory.CreateHasValueParameterRule(pid)
                    : ParameterFilterRuleFactory.CreateHasNoValueParameterRule(pid);
                return true;
            }
            if (value == null || value.Type == JTokenType.Null)
            {
                error = Err("INVALID_RULE", evaluator + " needs a value.");
                return false;
            }

            if (!TryStorage(doc, param, categoryIds, out var storage, out var spec2, out var storageError))
            {
                error = Err("PARAMETER_UNSUPPORTED", storageError);
                return false;
            }
            report["storage"] = storage.ToString();
            if (storage != StorageType.String && StringOnly.Contains(evaluator))
            {
                error = Err("INVALID_RULE", evaluator + " works only on text parameters; '" + param.Name + "' holds " + storage + ".");
                return false;
            }

            switch (storage)
            {
                case StorageType.String:
                    if (value.Type != JTokenType.String)
                    {
                        error = Err("INVALID_VALUE", "'" + param.Name + "' is text; give value as a string.");
                        return false;
                    }
                    var s = value.Value<string>();
                    report["value"] = s;
                    rule = StringRule(pid, evaluator, s);
                    return true;
                case StorageType.Integer:
                    int n;
                    if (value.Type == JTokenType.Boolean) n = value.Value<bool>() ? 1 : 0;
                    else if (value.Type == JTokenType.Integer && value.Value<long>() >= int.MinValue && value.Value<long>() <= int.MaxValue) n = value.Value<int>();
                    else
                    {
                        error = Err("INVALID_VALUE", "'" + param.Name + "' holds an integer (Yes/No is true/false); give an integer or boolean.");
                        return false;
                    }
                    report["value"] = n;
                    rule = IntRule(pid, evaluator, n);
                    return true;
                case StorageType.ElementId:
                    if (value.Type != JTokenType.Integer)
                    {
                        error = Err("INVALID_VALUE", "'" + param.Name + "' holds an element id; give the id as an integer.");
                        return false;
                    }
                    report["value"] = value.Value<long>();
                    rule = IdRule(pid, evaluator, RevitCompat.ToElementId(value.Value<long>()));
                    return true;
                case StorageType.Double:
                    if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)
                    {
                        error = Err("INVALID_VALUE", "'" + param.Name + "' is a number; give value as a number.");
                        return false;
                    }
                    if (!TryInternal(doc, spec2, value.Value<double>(), out var internalValue, out var unit, out var unitError))
                    {
                        error = Err("UNIT_UNKNOWN", unitError);
                        return false;
                    }
                    report["value"] = value.Value<double>();
                    report["unit"] = unit;
                    rule = DoubleRule(pid, evaluator, internalValue);
                    return true;
                default:
                    error = Err("PARAMETER_UNSUPPORTED", "'" + param.Name + "' has no value Revit can filter on.");
                    return false;
            }
        }

        /// <summary>Storage type and, for numbers, the data type (spec) used for unit conversion.</summary>
        private static bool TryStorage(Document doc, ParamInfo param, IList<ElementId> categoryIds,
            out StorageType storage, out ForgeTypeId spec, out string error)
        {
            storage = StorageType.None;
            spec = null;
            error = null;
            if (param.BuiltIn != null)
            {
                var bip = (BuiltInParameter)(int)RevitCompat.GetId(param.Id);
                storage = doc.get_TypeOfStorage(bip);
                if (storage == StorageType.Double)
                {
                    spec = SampleSpec(doc, bip, categoryIds);
                    if (spec == null)
                    {
                        error = "no element of the chosen categories carries '" + param.Name + "', so its unit is unknown.";
                        return false;
                    }
                }
                return true;
            }

            spec = (doc.GetElement(param.Id) as ParameterElement)?.GetDefinition()?.GetDataType();
            if (spec == null || spec.Empty())
            {
                error = "the data type of '" + param.Name + "' is unknown.";
                return false;
            }
            if (IsMeasurable(spec)) storage = StorageType.Double;
            else if (spec == SpecTypeId.String.Text || spec == SpecTypeId.String.MultilineText || spec == SpecTypeId.String.Url) storage = StorageType.String;
            else if (spec == SpecTypeId.Int.Integer || spec == SpecTypeId.Boolean.YesNo) storage = StorageType.Integer;
            else if (Category.IsBuiltInCategory(spec)) storage = StorageType.ElementId;
            else
            {
                error = "'" + param.Name + "' has data type " + spec.TypeId + ", which create_view_filter does not support.";
                return false;
            }
            return true;
        }

        private static bool IsMeasurable(ForgeTypeId spec)
        {
            try
            {
                return UnitUtils.IsMeasurableSpec(spec);
            }
            catch
            {
                return false; // Not a spec at all.
            }
        }

        private static ForgeTypeId SampleSpec(Document doc, BuiltInParameter bip, IList<ElementId> categoryIds)
        {
            foreach (var categoryId in categoryIds)
            {
                var samples = new[]
                {
                    new FilteredElementCollector(doc).OfCategoryId(categoryId).WhereElementIsNotElementType().FirstElement(),
                    new FilteredElementCollector(doc).OfCategoryId(categoryId).WhereElementIsElementType().FirstElement()
                };
                foreach (var sample in samples)
                {
                    var spec = sample?.get_Parameter(bip)?.Definition?.GetDataType();
                    if (spec != null && !spec.Empty()) return spec;
                }
            }
            return null;
        }

        private static bool TryInternal(Document doc, ForgeTypeId spec, double value, out double internalValue, out string unit, out string error)
        {
            error = null;
            if (spec == SpecTypeId.Length)
            {
                internalValue = Mm.ToFeet(value);
                unit = "mm";
            }
            else if (spec == SpecTypeId.Area)
            {
                internalValue = value / 0.09290304;
                unit = "m2";
            }
            else if (spec == SpecTypeId.Volume)
            {
                internalValue = value / 0.0283168466;
                unit = "m3";
            }
            else if (spec == SpecTypeId.Angle)
            {
                internalValue = value * Math.PI / 180.0;
                unit = "degrees";
            }
            else if (spec == SpecTypeId.Number)
            {
                internalValue = value;
                unit = "number";
            }
            else if (IsMeasurable(spec))
            {
                var unitId = doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId();
                internalValue = UnitUtils.ConvertToInternalUnits(value, unitId);
                unit = LabelUtils.GetLabelForUnit(unitId) + " (project display unit)";
            }
            else
            {
                internalValue = 0;
                unit = null;
                error = "data type " + spec.TypeId + " is not a measurable number.";
                return false;
            }
            internalValue = Math.Round(internalValue, 12, MidpointRounding.AwayFromZero);
            return true;
        }

        private static FilterRule StringRule(ElementId pid, string evaluator, string value)
        {
            switch (evaluator)
            {
                case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(pid, value);
                case "not_equals": return ParameterFilterRuleFactory.CreateNotEqualsRule(pid, value);
                case "greater": return ParameterFilterRuleFactory.CreateGreaterRule(pid, value);
                case "greater_or_equal": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(pid, value);
                case "less": return ParameterFilterRuleFactory.CreateLessRule(pid, value);
                case "less_or_equal": return ParameterFilterRuleFactory.CreateLessOrEqualRule(pid, value);
                case "contains": return ParameterFilterRuleFactory.CreateContainsRule(pid, value);
                case "not_contains": return ParameterFilterRuleFactory.CreateNotContainsRule(pid, value);
                case "begins_with": return ParameterFilterRuleFactory.CreateBeginsWithRule(pid, value);
                case "not_begins_with": return ParameterFilterRuleFactory.CreateNotBeginsWithRule(pid, value);
                case "ends_with": return ParameterFilterRuleFactory.CreateEndsWithRule(pid, value);
                case "not_ends_with": return ParameterFilterRuleFactory.CreateNotEndsWithRule(pid, value);
                default: throw new ArgumentException("evaluator " + evaluator);
            }
        }

        private static FilterRule IntRule(ElementId pid, string evaluator, int value)
        {
            switch (evaluator)
            {
                case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(pid, value);
                case "not_equals": return ParameterFilterRuleFactory.CreateNotEqualsRule(pid, value);
                case "greater": return ParameterFilterRuleFactory.CreateGreaterRule(pid, value);
                case "greater_or_equal": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(pid, value);
                case "less": return ParameterFilterRuleFactory.CreateLessRule(pid, value);
                case "less_or_equal": return ParameterFilterRuleFactory.CreateLessOrEqualRule(pid, value);
                default: throw new ArgumentException("evaluator " + evaluator);
            }
        }

        private static FilterRule DoubleRule(ElementId pid, string evaluator, double value)
        {
            switch (evaluator)
            {
                case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(pid, value, Epsilon);
                case "not_equals": return ParameterFilterRuleFactory.CreateNotEqualsRule(pid, value, Epsilon);
                case "greater": return ParameterFilterRuleFactory.CreateGreaterRule(pid, value, Epsilon);
                case "greater_or_equal": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(pid, value, Epsilon);
                case "less": return ParameterFilterRuleFactory.CreateLessRule(pid, value, Epsilon);
                case "less_or_equal": return ParameterFilterRuleFactory.CreateLessOrEqualRule(pid, value, Epsilon);
                default: throw new ArgumentException("evaluator " + evaluator);
            }
        }

        private static FilterRule IdRule(ElementId pid, string evaluator, ElementId value)
        {
            switch (evaluator)
            {
                case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(pid, value);
                case "not_equals": return ParameterFilterRuleFactory.CreateNotEqualsRule(pid, value);
                case "greater": return ParameterFilterRuleFactory.CreateGreaterRule(pid, value);
                case "greater_or_equal": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(pid, value);
                case "less": return ParameterFilterRuleFactory.CreateLessRule(pid, value);
                case "less_or_equal": return ParameterFilterRuleFactory.CreateLessOrEqualRule(pid, value);
                default: throw new ArgumentException("evaluator " + evaluator);
            }
        }
    }
}
