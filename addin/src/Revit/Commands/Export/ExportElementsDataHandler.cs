// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Export/ExportElementsDataHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: written to the exact path asked for (output_path, or output_folder + file_name) through a
// staging folder, so an existing file is replaced only with overwrite (rvt-mcp overwrote it); output_folder is
// created when missing; the category is a built-in name (OST_Walls) or a display name, and a miss lists the
// valid categories; lengths go through Mm (mm), areas m2, volumes m3, angles degrees; the reflection-based
// spec matching is replaced by asking Revit which units a spec accepts; requested parameters that no element
// has are reported. No transaction is opened.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Export
{
    public sealed class ExportElementsDataHandler : IRevitCommand
    {
        private const int MaxParameters = 200;

        public string Name => "export_elements_data";
        public string Description =>
            "Write parameter values of every instance of a category to a JSON or CSV file at output_path, or output_folder + file_name "
            + "(absolute; folder created if missing). category: built-in name such as OST_Walls, or the category's name. "
            + "Columns: id, name, type, level, then parameter_names (default: Mark). Lengths in mm, areas m2, volumes m3, angles degrees. "
            + "format: json or csv (default from the extension, else json). An existing file is replaced only with overwrite: true.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + ExportHelpers.OutputSchema + @",
            ""category"":{""type"":""string""},
            ""parameter_names"":{""type"":""array"",""items"":{""type"":""string""}},
            ""format"":{""type"":""string"",""enum"":[""json"",""csv""]}},
            ""required"":[""category""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!ExportHelpers.TryCategory(doc, p.Value<string>("category"), out var category, out var failure)) return failure;

            var path = p.Value<string>("output_path");
            var fileName = p.Value<string>("file_name");
            var format = p.Value<string>("format")
                ?? ((path ?? fileName ?? "").EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? "csv" : "json");
            var overwrite = p.Value<bool?>("overwrite") ?? false;
            if (!OutputFile.TryResolve(path, p.Value<string>("output_folder"), fileName, "." + format, overwrite, out var target, out failure)) return failure;

            var requested = (p["parameter_names"] as JArray)?.Select(t => (t.Value<string>() ?? "").Trim()).Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
            if (requested.Count == 0) requested.Add("Mark");
            if (requested.Count > MaxParameters) return CommandResult.Fail("INVALID_PARAMS: at most " + MaxParameters + " parameter_names.");
            var columns = new List<string> { "id", "name", "type", "level" };
            columns.AddRange(requested.Where(n => !columns.Contains(n, StringComparer.OrdinalIgnoreCase)));

            var elements = new FilteredElementCollector(doc).OfCategoryId(category.Id).WhereElementIsNotElementType().ToElements();
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<JObject>();
            foreach (var element in elements)
            {
                var row = new JObject
                {
                    ["id"] = RevitCompat.GetId(element.Id),
                    ["name"] = SafeName(element),
                    ["type"] = doc.GetElement(element.GetTypeId())?.Name,
                    ["level"] = LevelName(doc, element)
                };
                foreach (var column in columns.Skip(4))
                {
                    var parameter = element.LookupParameter(column);
                    if (parameter != null) found.Add(column);
                    row[column] = parameter == null ? JValue.CreateNull() : Value(parameter);
                }
                rows.Add(row);
            }

            var content = format == "csv" ? Csv(columns, rows) : new JArray(rows).ToString(Formatting.Indented);
            var result = ExportHelpers.ExportSingle(format, target, "export." + format, overwrite,
                staging => File.WriteAllText(Path.Combine(staging, "export." + format), content, new UTF8Encoding(false)),
                new
                {
                    category = category.Name,
                    element_count = rows.Count,
                    columns,
                    parameters_not_found = columns.Skip(4).Where(c => !found.Contains(c)).ToList()
                });
            return result;
        }

        private static JToken Value(Parameter parameter)
        {
            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        return parameter.AsString();
                    case StorageType.Integer:
                        return parameter.AsInteger();
                    case StorageType.ElementId:
                        var text = parameter.AsValueString();
                        return string.IsNullOrEmpty(text) ? (JToken)RevitCompat.GetIdOrNull(parameter.AsElementId()) : text;
                    case StorageType.Double:
                        return Double(parameter);
                    default:
                        return parameter.AsValueString();
                }
            }
            catch
            {
                return JValue.CreateNull();
            }
        }

        private static double Double(Parameter parameter)
        {
            var raw = parameter.AsDouble();
            ForgeTypeId spec = null;
            try { spec = parameter.Definition?.GetDataType(); }
            catch { spec = null; }
            if (spec == null || !UnitUtils.IsMeasurableSpec(spec)) return Math.Round(raw, 6);
            var units = UnitUtils.GetValidUnits(spec);
            if (spec == SpecTypeId.Length || units.Contains(UnitTypeId.Millimeters)) return Mm.FromFeet(raw);
            if (units.Contains(UnitTypeId.SquareMeters)) return Math.Round(UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.SquareMeters), 4);
            if (units.Contains(UnitTypeId.CubicMeters)) return Math.Round(UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.CubicMeters), 4);
            if (units.Contains(UnitTypeId.Degrees)) return Math.Round(UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.Degrees), 4);
            return Math.Round(raw, 6);
        }

        private static string SafeName(Element element)
        {
            try { return element.Name; }
            catch { return null; }
        }

        private static string LevelName(Document doc, Element element)
        {
            if (element.LevelId != ElementId.InvalidElementId && doc.GetElement(element.LevelId) is Level level) return level.Name;
            var p = element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                ?? element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                ?? element.get_Parameter(BuiltInParameter.LEVEL_PARAM);
            return p != null && p.StorageType == StorageType.ElementId ? (doc.GetElement(p.AsElementId()) as Level)?.Name : null;
        }

        private static string Csv(List<string> columns, List<JObject> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", columns.Select(Quote)));
            foreach (var row in rows)
                sb.AppendLine(string.Join(",", columns.Select(c =>
                {
                    var v = row[c];
                    if (v == null || v.Type == JTokenType.Null) return "";
                    return Quote(v.Type == JTokenType.Float
                        ? v.Value<double>().ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : v.ToString());
                })));
            return sb.ToString();
        }

        private static string Quote(string s) =>
            s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
