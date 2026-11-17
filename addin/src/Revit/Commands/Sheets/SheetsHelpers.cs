// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// files src/shared/Infrastructure/Responses/ResponsePaging.cs and the parameter helpers of
// src/shared/Handlers/Sheets/GetTitleblockParametersHandler.cs and SetTitleblockParametersHandler.cs, commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for Plinth: paging errors use "CODE: message"; unit conversion compares SpecTypeId values instead of
// matching type-id strings and sends lengths through Mm; a value of the wrong JSON type is an error instead
// of being parsed from its text; title block lookup on a sheet reports how many there are.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Sheets
{
    /// <summary>Paging, title block and parameter helpers shared by the sheet toolset (and analyze_sheet_layout).</summary>
    internal static class SheetsHelpers
    {
        private const double SquareMetresPerSquareFoot = 0.09290304;
        private const double CubicMetresPerCubicFoot = 0.028316846592;

        public sealed class PageRange
        {
            public int Start { get; set; }
            public int Max { get; set; }
        }

        public static bool TryPaging(JObject p, string startKey, string maxKey, int defaultMax, int hardMax, out PageRange range, out CommandResult failure)
        {
            range = new PageRange { Start = p.Value<int?>(startKey) ?? 0, Max = p.Value<int?>(maxKey) ?? defaultMax };
            failure = null;
            if (range.Start < 0) failure = CommandResult.Fail("INVALID_PARAMS: " + startKey + " must be 0 or more.");
            else if (range.Max < 1 || range.Max > hardMax) failure = CommandResult.Fail("INVALID_PARAMS: " + maxKey + " must be 1 to " + hardMax + ".");
            return failure == null;
        }

        /// <summary>The page's items, and paging metadata written into <paramref name="into"/> with the given prefix.</summary>
        public static List<T> Page<T>(IReadOnlyList<T> all, PageRange range, JObject into, string countKey, string startKey, string returnedKey, string nextKey)
        {
            var items = all.Skip(range.Start).Take(range.Max).ToList();
            var next = range.Start + items.Count;
            var truncated = next < all.Count;
            into[countKey] = all.Count;
            into[startKey] = range.Start;
            into[returnedKey] = items.Count;
            into["truncated"] = truncated;
            into[nextKey] = truncated ? (JToken)next : JValue.CreateNull();
            return items;
        }

        /// <summary>One viewport's geometry for get_viewport_geometry (rvt-mcp ViewportSheetSupport.SnapshotDto).</summary>
        public static object SnapshotDto(ViewportSnapshot s) => new
        {
            viewport_id = s.ViewportId,
            view_id = s.ViewId,
            view_name = s.ViewName,
            view_type = s.ViewType,
            scale = s.Scale,
            rotation = s.Rotation,
            pinned = s.Pinned,
            saved_position = s.SavedPosition,
            crop_active = s.CropActive,
            crop_visible = s.CropVisible,
            box_center_sheet_mm = new { x = ViewportSheetSupport.ToMm(s.BoxCenterX), y = ViewportSheetSupport.ToMm(s.BoxCenterY) },
            rect_source = s.RectSource,
            visible_rect_sheet_mm = ViewportSheetSupport.RectDto(s.Used),
            crop_rect_sheet_mm = ViewportSheetSupport.RectDto(s.Crop),
            view_outline_sheet_mm = ViewportSheetSupport.RectDto(s.Outline),
            annotation_overflow_mm = s.Overflow.HasValue
                ? new
                {
                    left = ViewportSheetSupport.ToMm(s.Overflow.Value.Left),
                    right = ViewportSheetSupport.ToMm(s.Overflow.Value.Right),
                    top = ViewportSheetSupport.ToMm(s.Overflow.Value.Top),
                    bottom = ViewportSheetSupport.ToMm(s.Overflow.Value.Bottom)
                }
                : null,
            warnings = s.Warnings
        };

        public static List<FamilyInstance> TitleBlocksOn(Document doc, ViewSheet sheet) =>
            new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType()
                .OfType<FamilyInstance>().OrderBy(t => RevitCompat.GetId(t.Id)).ToList();

        public static bool SheetNumberInUse(Document doc, string number, out ViewSheet existing)
        {
            existing = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .FirstOrDefault(s => string.Equals(s.SheetNumber, number, StringComparison.OrdinalIgnoreCase));
            return existing != null;
        }

        public static bool ContainsIgnoreCase(string text, string part) =>
            string.IsNullOrEmpty(part) || (text ?? "").IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>A parameter by exact name, else case-insensitively.</summary>
        public static Parameter FindParameter(Element element, string name)
        {
            var exact = element.LookupParameter(name);
            if (exact != null) return exact;
            foreach (Parameter param in element.Parameters)
                if (param.Definition != null && string.Equals(param.Definition.Name, name, StringComparison.OrdinalIgnoreCase))
                    return param;
            return null;
        }

        public static JObject ParameterDto(Parameter parameter)
        {
            JToken value = JValue.CreateNull();
            if (parameter.HasValue)
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String: value = parameter.AsString(); break;
                    case StorageType.Integer: value = parameter.AsInteger(); break;
                    case StorageType.Double: value = ToExternal(parameter, parameter.AsDouble()); break;
                    case StorageType.ElementId: value = RevitCompat.GetId(parameter.AsElementId()); break;
                }
            }
            return new JObject
            {
                ["name"] = parameter.Definition?.Name ?? "",
                ["storage_type"] = parameter.StorageType.ToString(),
                ["value"] = value,
                ["unit"] = UnitName(parameter),
                ["is_read_only"] = parameter.IsReadOnly,
                ["spec_type_id"] = SpecId(parameter)
            };
        }

        /// <summary>Sets a parameter from JSON. Lengths are mm, areas m², volumes m³, angles degrees.</summary>
        public static bool TrySet(Parameter parameter, JToken value, out string error)
        {
            error = null;
            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        if (value.Type != JTokenType.String && value.Type != JTokenType.Null && value.Type != JTokenType.Integer && value.Type != JTokenType.Float)
                        {
                            error = "the value must be text.";
                            return false;
                        }
                        return Confirm(parameter.Set(value.Type == JTokenType.Null ? "" : value.ToString()), out error);
                    case StorageType.Integer:
                        if (value.Type == JTokenType.Boolean) return Confirm(parameter.Set(value.Value<bool>() ? 1 : 0), out error);
                        if (value.Type != JTokenType.Integer)
                        {
                            error = "the value must be an integer (or true/false for a yes/no parameter).";
                            return false;
                        }
                        return Confirm(parameter.Set(value.Value<int>()), out error);
                    case StorageType.Double:
                        if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)
                        {
                            error = "the value must be a number" + (UnitName(parameter) is string unit ? " in " + unit : "") + ".";
                            return false;
                        }
                        return Confirm(parameter.Set(ToInternal(parameter, value.Value<double>())), out error);
                    case StorageType.ElementId:
                        if (value.Type != JTokenType.Integer)
                        {
                            error = "the value must be an element id (integer).";
                            return false;
                        }
                        return Confirm(parameter.Set(RevitCompat.ToElementId(value.Value<long>())), out error);
                    default:
                        error = "storage type " + parameter.StorageType + " cannot be set.";
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool Confirm(bool set, out string error)
        {
            error = set ? null : "Revit refused the value.";
            return set;
        }

        private static string SpecId(Parameter parameter)
        {
            try { return parameter.Definition?.GetDataType()?.TypeId; }
            catch (Exception) { return null; }
        }

        private static ForgeTypeId Spec(Parameter parameter)
        {
            try { return parameter.Definition?.GetDataType(); }
            catch (Exception) { return null; }
        }

        private static string UnitName(Parameter parameter)
        {
            if (parameter.StorageType != StorageType.Double) return null;
            var spec = Spec(parameter);
            if (spec == null) return null;
            if (spec == SpecTypeId.Length) return "mm";
            if (spec == SpecTypeId.Area) return "m2";
            if (spec == SpecTypeId.Volume) return "m3";
            if (spec == SpecTypeId.Angle) return "degrees";
            return null;
        }

        private static double ToExternal(Parameter parameter, double value)
        {
            switch (UnitName(parameter))
            {
                case "mm": return Mm.FromFeet(value, 3);
                case "m2": return value * SquareMetresPerSquareFoot;
                case "m3": return value * CubicMetresPerCubicFoot;
                case "degrees": return value * 180.0 / Math.PI;
                default: return value;
            }
        }

        private static double ToInternal(Parameter parameter, double value)
        {
            switch (UnitName(parameter))
            {
                case "mm": return Mm.ToFeet(value);
                case "m2": return value / SquareMetresPerSquareFoot;
                case "m3": return value / CubicMetresPerCubicFoot;
                case "degrees": return value * Math.PI / 180.0;
                default: return value;
            }
        }
    }
}
