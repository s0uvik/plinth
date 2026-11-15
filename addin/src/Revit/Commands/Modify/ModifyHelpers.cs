// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), the parameter-setting and category code
// repeated in src/shared/Handlers/Parameters/SetElementParameterValuesHandler.cs, SetTypeParameterValuesHandler.cs and
// src/shared/Handlers/Graphics/ColorElementsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: one copy for the modify toolset; doubles convert by the parameter's unit (length mm via Mm, area m2,
// volume m3, angle degrees; rvt-mcp took mm2 and mm3 through literal factors); one transaction wrapper that commits
// only when the body succeeds and checks the commit status.

using System;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Modify
{
    internal static class ModifyHelpers
    {
        public const string NoDocument = "NO_DOCUMENT: no document is open in Revit.";

        /// <summary>Runs body in one transaction; commits when it returns success, rolls back otherwise.</summary>
        public static CommandResult InTransaction(Document doc, string name, Func<CommandResult> body)
        {
            using (var tx = new Transaction(doc, "B2M: " + name))
            {
                try
                {
                    tx.Start();
                    var result = body();
                    if (!result.Success)
                    {
                        if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                        return result;
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures. Nothing was changed.");
                    return result;
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("MODIFY_FAILED: " + ex.Message + " Nothing was changed.");
                }
            }
        }

        public static bool TryIds(JObject p, string field, int max, out long[] ids, out CommandResult failure)
        {
            ids = p[field]?.ToObject<long[]>().Distinct().ToArray() ?? new long[0];
            failure = ids.Length == 0 || ids.Length > max
                ? CommandResult.Fail("INVALID_PARAMS: give 1 to " + max + " " + field + ".")
                : null;
            return failure == null;
        }

        public static bool TryCategory(Document doc, string name, out Category category, out CommandResult failure)
        {
            failure = null;
            category = null;
            if (name != null && name.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse(name, true, out BuiltInCategory bic))
                category = Category.GetCategory(doc, bic);
            if (category == null)
                category = doc.Settings.Categories.Cast<Category>()
                    .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (category != null) return true;
            failure = CommandResult.Fail("CATEGORY_NOT_FOUND: no category named '" + name + "'. Use a built-in name such as OST_Walls or a display name.",
                new JObject
                {
                    ["valid_categories"] = new JArray(doc.Settings.Categories.Cast<Category>()
                        .Where(c => c.CategoryType == CategoryType.Model).OrderBy(c => c.Name).Select(c => c.Name))
                });
            return false;
        }

        /// <summary>One parameter by name; ambiguous names (two parameters with one name) are an error, not a guess.</summary>
        public static Parameter FindParameter(Element element, string name, out string error)
        {
            error = null;
            var matches = element.GetParameters(name);
            if (matches.Count == 0)
                matches = element.Parameters.Cast<Parameter>()
                    .Where(x => string.Equals(x.Definition?.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1) return matches[0];
            error = matches.Count == 0 ? "parameter not found" : "several parameters are named '" + name + "'";
            return null;
        }

        public static string WireUnit(Parameter parameter)
        {
            ForgeTypeId spec;
            try
            {
                spec = parameter.Definition?.GetDataType();
                if (spec == null || !UnitUtils.IsMeasurableSpec(spec)) return "internal";
            }
            catch
            {
                return "internal";
            }
            var units = UnitUtils.GetValidUnits(spec);
            if (units.Any(u => u == UnitTypeId.Millimeters)) return "mm";
            if (units.Any(u => u == UnitTypeId.SquareMeters)) return "m2";
            if (units.Any(u => u == UnitTypeId.CubicMeters)) return "m3";
            if (units.Any(u => u == UnitTypeId.Degrees)) return "deg";
            return "internal";
        }

        public static double ToInternal(double wire, string unit)
        {
            switch (unit)
            {
                case "mm": return Mm.ToFeet(wire);
                case "m2": return UnitUtils.ConvertToInternalUnits(wire, UnitTypeId.SquareMeters);
                case "m3": return UnitUtils.ConvertToInternalUnits(wire, UnitTypeId.CubicMeters);
                case "deg": return UnitUtils.ConvertToInternalUnits(wire, UnitTypeId.Degrees);
                default: return wire;
            }
        }

        public static double ToWire(double internalValue, string unit)
        {
            switch (unit)
            {
                case "mm": return Mm.FromFeet(internalValue, 3);
                case "m2": return Math.Round(UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.SquareMeters), 6);
                case "m3": return Math.Round(UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.CubicMeters), 6);
                case "deg": return Math.Round(UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.Degrees), 6);
                default: return internalValue;
            }
        }

        public static JToken Value(Parameter parameter)
        {
            switch (parameter.StorageType)
            {
                case StorageType.String: return parameter.AsString() is string s ? new JValue(s) : JValue.CreateNull();
                case StorageType.Integer: return new JValue(parameter.AsInteger());
                case StorageType.Double: return new JValue(ToWire(parameter.AsDouble(), WireUnit(parameter)));
                case StorageType.ElementId: return new JValue(RevitCompat.GetId(parameter.AsElementId()));
                default: return JValue.CreateNull();
            }
        }

        public static string Display(Parameter parameter)
        {
            try
            {
                return parameter.AsValueString() ?? (parameter.StorageType == StorageType.String ? parameter.AsString() : null);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Sets one parameter from a string. valueType "auto" follows the storage type; doubles are read in the
        /// parameter's wire unit (mm, m2, m3, degrees). Returns null on success, else the reason.
        /// </summary>
        public static string TrySet(Parameter parameter, string value, string valueType, JObject row)
        {
            if (parameter.IsReadOnly) return "parameter is read-only";
            var storage = parameter.StorageType;
            var effective = valueType == "auto" || string.IsNullOrEmpty(valueType) ? StorageName(storage) : valueType;
            if (effective == null) return "unsupported storage type " + storage;
            if (effective != StorageName(storage)) return "valueType " + effective + " does not fit storage type " + storage;
            row["valueTypeUsed"] = effective;
            bool accepted;
            switch (storage)
            {
                case StorageType.String:
                    accepted = parameter.Set(value);
                    break;
                case StorageType.Integer:
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return "value must be an integer";
                    accepted = parameter.Set(i);
                    break;
                case StorageType.Double:
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || double.IsNaN(d) || double.IsInfinity(d))
                        return "value must be a number";
                    var unit = WireUnit(parameter);
                    row["inputUnit"] = unit;
                    accepted = parameter.Set(ToInternal(d, unit));
                    break;
                case StorageType.ElementId:
                    if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return "value must be an element ID";
                    accepted = parameter.Set(RevitCompat.ToElementId(id));
                    break;
                default:
                    return "unsupported storage type " + storage;
            }
            return accepted ? null : "Revit rejected the value";
        }

        private static string StorageName(StorageType storage)
        {
            switch (storage)
            {
                case StorageType.String: return "string";
                case StorageType.Integer: return "integer";
                case StorageType.Double: return "double";
                case StorageType.ElementId: return "elementId";
                default: return null;
            }
        }

        public static JObject Point(XYZ point) => point == null ? null : new JObject
        {
            ["x"] = Mm.FromFeet(point.X, 3),
            ["y"] = Mm.FromFeet(point.Y, 3),
            ["z"] = Mm.FromFeet(point.Z, 3)
        };

        public static ElementId SolidFill(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill)?.Id ?? ElementId.InvalidElementId;

        /// <summary>Projection lines and a solid surface fill in one colour (rvt-mcp set no fill pattern, so surfaces stayed uncoloured).</summary>
        public static OverrideGraphicSettings ColorOverride(Document doc, Color color)
        {
            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(color);
            ogs.SetSurfaceForegroundPatternColor(color);
            var solid = SolidFill(doc);
            if (solid != ElementId.InvalidElementId) ogs.SetSurfaceForegroundPatternId(solid);
            return ogs;
        }
    }
}
