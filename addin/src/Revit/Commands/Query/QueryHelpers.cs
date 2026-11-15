// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), files
// src/shared/Infrastructure/Responses/ResponsePaging.cs and the category / parameter-value code repeated in
// src/shared/Handlers/Elements/AiElementFilterHandler.cs and src/shared/Handlers/Parameters/GetElementParametersHandler.cs,
// commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: one copy for the query toolset; categories resolve by OST_ name or display name and a miss lists
// the valid names; double values convert by unit (length mm via Mm, area m2, volume m3, angle degrees) instead of
// literal 304.8 factors and spec-name string matching.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Query
{
    internal static class QueryHelpers
    {
        public const string PagingSchema = @"""start_index"":{""type"":""integer""},""max_results"":{""type"":""integer""}";

        public static bool TryPaging(JObject p, int defaultSize, int max, out int start, out int size, out CommandResult failure)
        {
            start = p.Value<int?>("start_index") ?? 0;
            size = p.Value<int?>("max_results") ?? defaultSize;
            failure = null;
            if (start < 0) failure = CommandResult.Fail("INVALID_PARAMS: start_index must be 0 or more.");
            else if (size < 1 || size > max) failure = CommandResult.Fail("INVALID_PARAMS: max_results must be between 1 and " + max + ".");
            return failure == null;
        }

        /// <summary>The page plus the paging fields every paged result carries.</summary>
        public static JObject Page<T>(IReadOnlyList<T> all, int start, int size, string itemsKey, Func<T, JToken> map)
        {
            var items = all.Skip(start).Take(size).ToList();
            var next = start + items.Count;
            return new JObject
            {
                ["total_count"] = all.Count,
                ["start_index"] = start,
                ["returned_count"] = items.Count,
                ["truncated"] = next < all.Count,
                ["next_index"] = next < all.Count ? new JValue(next) : JValue.CreateNull(),
                [itemsKey] = new JArray(items.Select(map))
            };
        }

        /// <summary>A category by built-in name (OST_Walls) or by its display name in this Revit language.</summary>
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
                        .Where(c => c.CategoryType == CategoryType.Model)
                        .OrderBy(c => c.Name).Select(c => c.Name))
                });
            return false;
        }

        public static long? IdOrNull(ElementId id) =>
            id == null || id == ElementId.InvalidElementId ? (long?)null : RevitCompat.GetId(id);

        public static JToken Id(ElementId id) => IdOrNull(id) is long v ? new JValue(v) : JValue.CreateNull();

        public static string NameOf(Element element)
        {
            try
            {
                return element?.Name;
            }
            catch
            {
                return null;
            }
        }

        public static JObject Point(XYZ point) => point == null ? null : new JObject
        {
            ["x"] = Mm.FromFeet(point.X, 3),
            ["y"] = Mm.FromFeet(point.Y, 3),
            ["z"] = Mm.FromFeet(point.Z, 3)
        };

        public static int? WorksetIdOf(Element element)
        {
            try
            {
                var id = element.WorksetId;
                return id == null || id.IntegerValue <= 0 ? (int?)null : id.IntegerValue;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The unit B2M uses on the wire for a double parameter: "mm", "m2", "m3", "deg", or "internal".</summary>
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

        /// <summary>The value in wire units (doubles converted), an element ID as a number, or text.</summary>
        public static JToken Value(Parameter parameter)
        {
            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String: return parameter.AsString() is string s ? new JValue(s) : JValue.CreateNull();
                    case StorageType.Integer: return new JValue(parameter.AsInteger());
                    case StorageType.Double: return new JValue(ToWire(parameter.AsDouble(), WireUnit(parameter)));
                    case StorageType.ElementId: return Id(parameter.AsElementId());
                    default: return JValue.CreateNull();
                }
            }
            catch
            {
                return JValue.CreateNull();
            }
        }

        /// <summary>Text for matching and grouping: element names for IDs, wire-unit numbers for doubles.</summary>
        public static string Text(Document doc, Parameter parameter)
        {
            switch (parameter.StorageType)
            {
                case StorageType.String: return parameter.AsString();
                case StorageType.Integer: return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
                case StorageType.Double:
                    return ToWire(parameter.AsDouble(), WireUnit(parameter)).ToString("0.###", CultureInfo.InvariantCulture);
                case StorageType.ElementId:
                    var id = parameter.AsElementId();
                    return NameOf(doc.GetElement(id)) ?? RevitCompat.GetId(id).ToString(CultureInfo.InvariantCulture);
                default: return null;
            }
        }

        public static string DisplayValue(Parameter parameter)
        {
            try
            {
                var value = parameter.AsValueString();
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Full description of one parameter, shared by get_element_parameters and get_type_parameters.</summary>
        public static JObject Describe(Document doc, Parameter parameter)
        {
            var definition = parameter.Definition;
            var builtIn = (definition as InternalDefinition)?.BuiltInParameter ?? BuiltInParameter.INVALID;
            string spec = null;
            try
            {
                spec = definition?.GetDataType()?.TypeId;
            }
            catch
            {
                // Some definitions have no data type.
            }
            var result = new JObject
            {
                ["name"] = definition?.Name,
                ["id"] = Id(parameter.Id),
                ["builtInParameter"] = builtIn == BuiltInParameter.INVALID ? null : builtIn.ToString(),
                ["isShared"] = parameter.IsShared,
                ["guid"] = parameter.IsShared ? parameter.GUID.ToString() : null,
                ["storageType"] = parameter.StorageType.ToString(),
                ["specId"] = string.IsNullOrEmpty(spec) ? null : spec,
                ["isReadOnly"] = parameter.IsReadOnly,
                ["hasValue"] = parameter.HasValue,
                ["value"] = Value(parameter),
                ["displayValue"] = DisplayValue(parameter)
            };
            if (parameter.StorageType == StorageType.Double)
            {
                result["unit"] = WireUnit(parameter);
                result["internalValue"] = parameter.AsDouble();
            }
            if (parameter.StorageType == StorageType.ElementId && doc.GetElement(parameter.AsElementId()) is Element referenced)
                result["referencedElement"] = new JObject
                {
                    ["elementId"] = RevitCompat.GetId(referenced.Id),
                    ["name"] = NameOf(referenced),
                    ["category"] = referenced.Category?.Name
                };
            return result;
        }

        public static JArray Parameters(Document doc, Element element, bool includeReadOnly, out JArray errors)
        {
            var list = new JArray();
            errors = new JArray();
            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter == null || (!includeReadOnly && parameter.IsReadOnly)) continue;
                try
                {
                    list.Add(Describe(doc, parameter));
                }
                catch (Exception ex)
                {
                    errors.Add(new JObject { ["name"] = parameter.Definition?.Name, ["error"] = ex.Message });
                }
            }
            return new JArray(list.OrderBy(t => t.Value<string>("name"), StringComparer.OrdinalIgnoreCase));
        }
    }
}
