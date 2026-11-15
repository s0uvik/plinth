// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), files
// src/shared/Infrastructure/SharedParameterGuid.cs, src/shared/Handlers/Parameters/SetParameterValueByGuidHandler.cs,
// src/shared/Handlers/Parameters/CreateSharedParameterHandler.cs (data type aliases) and
// src/shared/Handlers/Parameters/BindSharedParameterHandler.cs (category lookup), commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for B2M: one place for parameter values in wire units (mm, m2, m3, degrees; lengths through Mm),
// shared parameter file access that always restores the user's configured file, and lookups that
// return the valid options on a miss. rvt-mcp converted area and volume with mm-based factors under
// m2/m3 labels; this uses Revit's own unit conversion.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Parameters
{
    internal static class ParametersHelpers
    {
        public const int MaxOptions = 100;

        public static readonly string[] UnitNames = { "auto", "internal", "mm", "m2", "m3", "deg" };

        // ---- shared parameter GUIDs -------------------------------------------------------

        /// <summary>The shared-parameter GUID of a definition, or null for a project parameter.</summary>
        public static Guid? SharedGuidOf(Document doc, Definition definition)
        {
            if (definition is ExternalDefinition external) return external.GUID;
            if (definition is InternalDefinition internalDefinition)
                return (doc.GetElement(internalDefinition.Id) as SharedParameterElement)?.GuidValue;
            return null;
        }

        public static bool TryGuid(string text, out Guid guid, out CommandResult failure)
        {
            failure = null;
            if (Guid.TryParse(text ?? string.Empty, out guid)) return true;
            failure = CommandResult.Fail("INVALID_PARAMS: '" + text + "' is not a GUID such as 0f3c2b1a-1111-2222-3333-444455556666.");
            return false;
        }

        /// <summary>Each binding in the document with its definition.</summary>
        public static List<KeyValuePair<Definition, ElementBinding>> Bindings(Document doc)
        {
            var list = new List<KeyValuePair<Definition, ElementBinding>>();
            var iterator = doc.ParameterBindings.ForwardIterator();
            iterator.Reset();
            while (iterator.MoveNext())
            {
                if (iterator.Key != null && iterator.Current is ElementBinding binding)
                    list.Add(new KeyValuePair<Definition, ElementBinding>(iterator.Key, binding));
            }
            return list;
        }

        public static string KindOf(Binding binding) => binding is TypeBinding ? "type" : "instance";

        public static JArray CategoriesJson(ElementBinding binding)
        {
            var list = new List<Category>();
            if (binding?.Categories != null)
                foreach (Category c in binding.Categories)
                    if (c != null) list.Add(c);
            return CategoriesJson(list);
        }

        public static JArray CategoriesJson(IEnumerable<Category> categories) =>
            new JArray(categories.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => new JObject
            {
                ["id"] = RevitCompat.GetId(c.Id),
                ["name"] = c.Name,
                ["builtInCategory"] = Lookup.BuiltInName(c)
            }));

        public static bool RequireProject(Document doc, out CommandResult failure)
        {
            failure = doc.IsFamilyDocument
                ? CommandResult.Fail("NOT_A_PROJECT: the active document is a family; project parameter bindings exist only in projects.")
                : null;
            return failure == null;
        }

        // ---- categories -------------------------------------------------------------------

        /// <summary>A category by built-in name (OST_Doors) or display name, that can take bound parameters.</summary>
        public static bool TryBindableCategory(Document doc, string input, out Category category, out CommandResult failure)
        {
            category = null;
            failure = null;
            if (!string.IsNullOrWhiteSpace(input))
            {
                if (input.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)
                    && Enum.TryParse(input, true, out BuiltInCategory bic) && Enum.IsDefined(typeof(BuiltInCategory), bic))
                {
                    try { category = Category.GetCategory(doc, bic); }
                    catch { category = null; }
                }
                if (category == null)
                {
                    foreach (Category c in doc.Settings.Categories)
                    {
                        if (string.Equals(c.Name, input, StringComparison.OrdinalIgnoreCase))
                        {
                            category = c;
                            break;
                        }
                    }
                }
            }
            if (category != null && category.AllowsBoundParameters) return true;

            var valid = new List<Category>();
            foreach (Category c in doc.Settings.Categories)
                if (c.AllowsBoundParameters) valid.Add(c);
            failure = CommandResult.Fail(category == null
                    ? "CATEGORY_NOT_FOUND: no category named '" + input + "'. Use a built-in name such as OST_Doors or a display name."
                    : "CATEGORY_NOT_BINDABLE: category '" + category.Name + "' cannot take bound parameters.",
                new JObject
                {
                    ["valid_categories"] = new JArray(valid.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Take(300)
                        .Select(c => Lookup.BuiltInName(c) ?? c.Name))
                });
            category = null;
            return false;
        }

        // ---- data types and groups --------------------------------------------------------

        public static readonly string[] DataTypeAliases =
            { "text", "integer", "number", "length", "area", "volume", "angle", "yesno", "url", "material" };

        /// <summary>A spec from an alias (text, length, yesno...) or a Forge spec id, with or without its version suffix.</summary>
        public static bool TryDataType(string input, out ForgeTypeId spec, out CommandResult failure)
        {
            failure = null;
            spec = null;
            switch ((input ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "text": case "string": spec = SpecTypeId.String.Text; break;
                case "integer": case "int": spec = SpecTypeId.Int.Integer; break;
                case "number": case "double": spec = SpecTypeId.Number; break;
                case "length": spec = SpecTypeId.Length; break;
                case "area": spec = SpecTypeId.Area; break;
                case "volume": spec = SpecTypeId.Volume; break;
                case "angle": spec = SpecTypeId.Angle; break;
                case "yesno": case "boolean": case "bool": spec = SpecTypeId.Boolean.YesNo; break;
                case "url": spec = SpecTypeId.String.Url; break;
                case "material": spec = SpecTypeId.Reference.Material; break;
            }
            if (spec != null) return true;

            var raw = (input ?? string.Empty).Trim();
            var all = SpecUtils.GetAllSpecs();
            spec = all.FirstOrDefault(s => string.Equals(s.TypeId, raw, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(s => s.TypeId.StartsWith(raw + "-", StringComparison.OrdinalIgnoreCase));
            if (spec != null) return true;
            failure = CommandResult.Fail("INVALID_DATA_TYPE: '" + input + "' is not a parameter data type.",
                new JObject
                {
                    ["aliases"] = new JArray(DataTypeAliases),
                    ["note"] = "Or pass a Forge spec id such as " + SpecTypeId.Length.TypeId + "."
                });
            return false;
        }

        /// <summary>A built-in parameter group from its Forge id; empty means Data.</summary>
        public static bool TryGroup(string input, out ForgeTypeId group, out CommandResult failure)
        {
            failure = null;
            if (string.IsNullOrWhiteSpace(input))
            {
                group = GroupTypeId.Data;
                return true;
            }
            var raw = input.Trim();
            var all = ParameterUtils.GetAllBuiltInGroups();
            group = all.FirstOrDefault(g => string.Equals(g.TypeId, raw, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(g => g.TypeId.StartsWith(raw + "-", StringComparison.OrdinalIgnoreCase));
            if (group != null) return true;
            failure = CommandResult.Fail("INVALID_GROUP: '" + input + "' is not a built-in parameter group. Omit it for Data.",
                new JObject { ["valid_groups"] = new JArray(all.Select(g => g.TypeId).OrderBy(s => s).Take(200)) });
            return false;
        }

        public static string SpecLabel(ForgeTypeId spec)
        {
            try { return spec == null || string.IsNullOrEmpty(spec.TypeId) ? null : LabelUtils.GetLabelForSpec(spec); }
            catch { return null; }
        }

        public static string GroupLabel(ForgeTypeId group)
        {
            try { return group == null || string.IsNullOrEmpty(group.TypeId) ? null : LabelUtils.GetLabelForGroup(group); }
            catch { return null; }
        }

        // ---- shared parameter file --------------------------------------------------------

        /// <summary>
        /// Opens a shared parameter file. When a path other than the user's configured one is given,
        /// Application.SharedParametersFilename points at it only until Dispose, which restores the
        /// user's setting (rvt-mcp did the same, but create_shared_parameter left it changed on early returns).
        /// </summary>
        public sealed class SharedParameterFile : IDisposable
        {
            private readonly Application _app;
            private readonly string _original;
            private readonly bool _switched;

            public DefinitionFile File { get; private set; }
            public string Path { get; private set; }

            private SharedParameterFile(Application app, string path, bool switched)
            {
                _app = app;
                _original = app.SharedParametersFilename;
                _switched = switched;
                Path = path;
            }

            public static bool TryOpen(Application app, string customPath, bool createIfMissing, out SharedParameterFile file,
                out bool created, out CommandResult failure)
            {
                file = null;
                created = false;
                failure = null;
                var configured = app.SharedParametersFilename;
                var path = string.IsNullOrWhiteSpace(customPath) ? configured : customPath.Trim();
                if (string.IsNullOrWhiteSpace(path))
                {
                    failure = CommandResult.Fail("NO_SHARED_PARAMETER_FILE: Revit has no shared parameter file set; pass sharedParameterFilePath.");
                    return false;
                }
                var custom = !string.IsNullOrWhiteSpace(customPath);
                if (!IsAbsolute(path) || (custom && !path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
                {
                    failure = CommandResult.Fail(custom
                        ? "INVALID_PATH: sharedParameterFilePath must be an absolute path to a .txt file."
                        : "INVALID_PATH: Revit's configured shared parameter file '" + path + "' is not an absolute path; pass sharedParameterFilePath.");
                    return false;
                }
                try { path = System.IO.Path.GetFullPath(path); }
                catch (Exception ex)
                {
                    failure = CommandResult.Fail("INVALID_PATH: " + ex.Message);
                    return false;
                }
                if (!System.IO.File.Exists(path))
                {
                    if (!createIfMissing)
                    {
                        failure = CommandResult.Fail("FILE_NOT_FOUND: shared parameter file " + path + " does not exist"
                            + " (pass createFileIfMissing: true to create it).");
                        return false;
                    }
                    try
                    {
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                        using (System.IO.File.Create(path)) { }
                        created = true;
                    }
                    catch (Exception ex)
                    {
                        failure = CommandResult.Fail("FILE_WRITE_FAILED: could not create " + path + ": " + ex.Message);
                        return false;
                    }
                }

                var switched = !string.Equals(path, configured, StringComparison.OrdinalIgnoreCase);
                file = new SharedParameterFile(app, path, switched);
                try
                {
                    if (switched) app.SharedParametersFilename = path;
                    file.File = app.OpenSharedParameterFile();
                }
                catch (Exception ex)
                {
                    file.Dispose();
                    file = null;
                    failure = CommandResult.Fail("SHARED_PARAMETER_FILE_INVALID: Revit could not open " + path + ": " + ex.Message);
                    return false;
                }
                if (file.File == null)
                {
                    file.Dispose();
                    file = null;
                    failure = CommandResult.Fail("SHARED_PARAMETER_FILE_INVALID: Revit could not read " + path + " as a shared parameter file.");
                    return false;
                }
                return true;
            }

            public ExternalDefinition Find(Guid guid, out DefinitionGroup owner)
            {
                owner = null;
                foreach (DefinitionGroup group in File.Groups)
                    foreach (Definition definition in group.Definitions)
                        if (definition is ExternalDefinition external && external.GUID == guid)
                        {
                            owner = group;
                            return external;
                        }
                return null;
            }

            public void Dispose()
            {
                if (!_switched) return;
                try { _app.SharedParametersFilename = _original ?? string.Empty; }
                catch
                {
                    // Revit refuses an empty name on some builds; the user had no file configured anyway.
                }
            }
        }

        public static bool IsAbsolute(string path) =>
            !string.IsNullOrEmpty(path)
            && ((path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
                || path.StartsWith(@"\\", StringComparison.Ordinal));

        // ---- parameter values in wire units ---------------------------------------------

        private enum Kind { Other, Length, Area, Volume, Angle }

        private static Kind KindOf(Parameter p)
        {
            ForgeTypeId spec;
            try { spec = p.Definition?.GetDataType(); }
            catch { return Kind.Other; }
            if (spec == null || string.IsNullOrEmpty(spec.TypeId)) return Kind.Other;
            if (spec.Equals(SpecTypeId.Length)) return Kind.Length;
            if (spec.Equals(SpecTypeId.Area)) return Kind.Area;
            if (spec.Equals(SpecTypeId.Volume)) return Kind.Volume;
            if (spec.Equals(SpecTypeId.Angle)) return Kind.Angle;
            return Kind.Other;
        }

        private static bool IsUnitless(Parameter p)
        {
            try
            {
                var spec = p.Definition?.GetDataType();
                return spec != null && spec.Equals(SpecTypeId.Number);
            }
            catch { return false; }
        }

        /// <summary>The value in wire units (mm, m2, m3, degrees); other doubles stay in Revit's internal units.</summary>
        public static JObject Read(Document doc, Parameter p)
        {
            var o = new JObject { ["storageType"] = p.StorageType.ToString() };
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String:
                        o["value"] = p.AsString();
                        break;
                    case StorageType.Integer:
                        o["value"] = p.AsInteger();
                        break;
                    case StorageType.Double:
                        var raw = p.AsDouble();
                        switch (KindOf(p))
                        {
                            case Kind.Length: o["value"] = Mm.FromFeet(raw, 3); o["unit"] = "mm"; break;
                            case Kind.Area: o["value"] = Math.Round(UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.SquareMeters), 6); o["unit"] = "m2"; break;
                            case Kind.Volume: o["value"] = Math.Round(UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.CubicMeters), 6); o["unit"] = "m3"; break;
                            case Kind.Angle: o["value"] = Math.Round(UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.Degrees), 6); o["unit"] = "deg"; break;
                            default: o["value"] = raw; o["unit"] = IsUnitless(p) ? null : "internal"; break;
                        }
                        break;
                    case StorageType.ElementId:
                        var id = p.AsElementId();
                        o["value"] = id == null ? null : (JToken)RevitCompat.GetId(id);
                        var target = id == null || id == ElementId.InvalidElementId ? null : doc.GetElement(id);
                        if (target != null) o["elementName"] = target.Name;
                        break;
                    default:
                        o["value"] = null;
                        break;
                }
                var display = p.AsValueString();
                if (!string.IsNullOrEmpty(display)) o["display"] = display;
            }
            catch
            {
                o["value"] = null;
            }
            return o;
        }

        /// <summary>
        /// Sets a parameter from a JSON value. Doubles are read in wire units: unit "auto" picks mm, m2,
        /// m3 or deg from the parameter's data type and refuses other measured types (pass "internal").
        /// Nothing is set when the value does not fit.
        /// </summary>
        public static bool TrySet(Document doc, Parameter p, JToken value, string unit, out string unitUsed, out string error)
        {
            unitUsed = null;
            error = null;
            if (p.IsReadOnly)
            {
                error = "parameter '" + p.Definition?.Name + "' is read-only";
                return false;
            }
            if (value == null || value.Type == JTokenType.Null)
            {
                error = "value is null";
                return false;
            }
            unit = string.IsNullOrEmpty(unit) ? "auto" : unit.ToLowerInvariant();
            bool ok;
            switch (p.StorageType)
            {
                case StorageType.String:
                    if (value.Type != JTokenType.String && value.Type != JTokenType.Integer && value.Type != JTokenType.Float)
                    {
                        error = "a text parameter needs a string";
                        return false;
                    }
                    ok = p.Set(value.Type == JTokenType.String ? value.Value<string>() : value.ToString(Newtonsoft.Json.Formatting.None));
                    break;
                case StorageType.Integer:
                    if (!TryInt(value, out var i))
                    {
                        error = "an integer parameter needs a whole number (or true/false for Yes/No)";
                        return false;
                    }
                    ok = p.Set(i);
                    break;
                case StorageType.Double:
                    if (!TryDouble(value, out var d))
                    {
                        error = "a number parameter needs a number";
                        return false;
                    }
                    if (!TryToInternal(p, d, unit, out var internalValue, out unitUsed, out error)) return false;
                    ok = p.Set(internalValue);
                    break;
                case StorageType.ElementId:
                    if (!TryLong(value, out var l))
                    {
                        error = "an element parameter needs an element id (-1 for none)";
                        return false;
                    }
                    var id = RevitCompat.ToElementId(l);
                    if (l != -1 && doc.GetElement(id) == null)
                    {
                        error = "element " + l + " does not exist";
                        return false;
                    }
                    ok = p.Set(l == -1 ? ElementId.InvalidElementId : id);
                    break;
                default:
                    error = "parameter '" + p.Definition?.Name + "' has no settable value";
                    return false;
            }
            if (!ok) error = "Revit rejected the value";
            return ok;
        }

        private static bool TryToInternal(Parameter p, double value, string unit, out double result, out string unitUsed, out string error)
        {
            result = 0;
            error = null;
            var kind = KindOf(p);
            if (unit == "auto")
            {
                switch (kind)
                {
                    case Kind.Length: unit = "mm"; break;
                    case Kind.Area: unit = "m2"; break;
                    case Kind.Volume: unit = "m3"; break;
                    case Kind.Angle: unit = "deg"; break;
                    default:
                        if (!IsUnitless(p))
                        {
                            unitUsed = null;
                            error = "cannot infer the unit of '" + p.Definition?.Name + "'; pass unit: \"internal\" to give Revit's internal value";
                            return false;
                        }
                        unit = "internal";
                        break;
                }
            }
            unitUsed = unit;
            var expected = unit == "mm" ? Kind.Length : unit == "m2" ? Kind.Area : unit == "m3" ? Kind.Volume : unit == "deg" ? Kind.Angle : Kind.Other;
            if (unit != "internal" && expected != kind)
            {
                error = "unit '" + unit + "' does not match the data type of '" + p.Definition?.Name + "'";
                return false;
            }
            switch (unit)
            {
                case "internal": result = value; return true;
                case "mm": result = Mm.ToFeet(value); return true;
                case "m2": result = UnitUtils.ConvertToInternalUnits(value, UnitTypeId.SquareMeters); return true;
                case "m3": result = UnitUtils.ConvertToInternalUnits(value, UnitTypeId.CubicMeters); return true;
                case "deg": result = UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Degrees); return true;
                default:
                    error = "unit must be one of " + string.Join(", ", UnitNames);
                    return false;
            }
        }

        private static bool TryInt(JToken v, out int result)
        {
            result = 0;
            switch (v.Type)
            {
                case JTokenType.Boolean: result = v.Value<bool>() ? 1 : 0; return true;
                case JTokenType.Integer:
                    var l = v.Value<long>();
                    if (l < int.MinValue || l > int.MaxValue) return false;
                    result = (int)l;
                    return true;
                case JTokenType.String:
                    return int.TryParse(v.Value<string>().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
                default: return false;
            }
        }

        private static bool TryLong(JToken v, out long result)
        {
            result = 0;
            if (v.Type == JTokenType.Integer)
            {
                result = v.Value<long>();
                return true;
            }
            return v.Type == JTokenType.String
                && long.TryParse(v.Value<string>().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        private static bool TryDouble(JToken v, out double result)
        {
            result = 0;
            if (v.Type == JTokenType.Integer || v.Type == JTokenType.Float)
            {
                result = v.Value<double>();
                return !double.IsNaN(result) && !double.IsInfinity(result);
            }
            return v.Type == JTokenType.String
                && double.TryParse(v.Value<string>().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        /// <summary>A parameter by exact name, else by case-insensitive name; null when absent or ambiguous by case.</summary>
        public static Parameter FindByName(Element element, string name)
        {
            var exact = element.LookupParameter(name);
            if (exact != null) return exact;
            Parameter found = null;
            foreach (Parameter p in element.Parameters)
            {
                if (p?.Definition?.Name == null || !string.Equals(p.Definition.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (found != null) return null;
                found = p;
            }
            return found;
        }

        public static JArray ParameterNames(Element element) =>
            new JArray(element.Parameters.Cast<Parameter>().Where(p => p?.Definition?.Name != null && !p.IsReadOnly)
                .Select(p => p.Definition.Name).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(200));
    }
}
