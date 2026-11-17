// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// files src/shared/Handlers/Workflows/WorkflowSupport.cs and src/shared/Handlers/Workflows/ClashDetectionHandler.cs,
// commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: categories resolve by BuiltInCategory name (OST_Walls) or exact category name and a miss
// lists the valid model categories; units go through Mm and the Plinth m2/m3 factors (rvt-mcp used UnitUtils and
// literal 304.8); file writes go through Export/OutputFile (exact path, no overwrite without overwrite: true);
// the clash check is a compact local copy of clash_detection (bounding-box prefilter, then solid intersection)
// with element and candidate caps, so the workflows do not depend on the geometry toolset being ported in parallel;
// sheet placement uses a grid inside the title block frame instead of fixed foot offsets.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Plinth.Revit.Commands.Export;
using Plinth.Revit.Commands.Sheets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Workflows
{
    using View = Autodesk.Revit.DB.View;

    internal static class WorkflowsHelpers
    {
        public const double SquareMetresPerSquareFoot = 0.09290304;
        public const double CubicMetresPerCubicFoot = 0.0283168466;
        private const int MaxOptions = 150;

        // ---- report envelope -------------------------------------------------------------------------

        public static JObject Step(string name, string tool, string status, string summary, object result, string error = null) =>
            new JObject
            {
                ["name"] = name,
                ["tool"] = tool,
                ["status"] = status,
                ["inputs_summary"] = summary,
                ["result"] = result == null ? JValue.CreateNull() : result as JToken ?? JToken.FromObject(result),
                ["error"] = error == null ? JValue.CreateNull() : new JValue(error)
            };

        public static JObject Rollback(string strategy, bool rolledBack, string reason) =>
            new JObject { ["strategy"] = strategy, ["rolled_back"] = rolledBack, ["reason"] = reason ?? "" };

        public static JObject Envelope(string workflow, bool dryRun, string status, JArray steps,
            IEnumerable<long> createdIds, IEnumerable<long> modifiedIds, IEnumerable<string> warnings, JObject rollback) =>
            new JObject
            {
                ["workflow"] = workflow,
                ["dry_run"] = dryRun,
                ["status"] = status,
                ["steps"] = steps ?? new JArray(),
                ["created_ids"] = new JArray((createdIds ?? Enumerable.Empty<long>()).Distinct()),
                ["modified_ids"] = new JArray((modifiedIds ?? Enumerable.Empty<long>()).Distinct()),
                ["warnings"] = new JArray(warnings ?? Enumerable.Empty<string>()),
                ["rollback"] = rollback ?? Rollback("None", false, "")
            };

        /// <summary>Runs another add-in command in-process; an exception becomes a failed result.</summary>
        public static CommandResult Invoke(IRevitCommand command, UIApplication app, JObject parameters)
        {
            try
            {
                return command.Execute(app, parameters);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("COMMAND_FAILED: " + command.Name + ": " + ex.Message);
            }
        }

        // ---- names and categories --------------------------------------------------------------------

        public static string SafeName(Element element)
        {
            try { return element?.Name; }
            catch { return null; }
        }

        public static string SafeCategoryName(Element element)
        {
            try { return element?.Category?.Name; }
            catch { return null; }
        }

        public static string UniqueName(IEnumerable<string> existingNames, string baseName)
        {
            var existing = new HashSet<string>(existingNames.Where(n => !string.IsNullOrEmpty(n)), StringComparer.OrdinalIgnoreCase);
            var candidate = baseName;
            for (var i = 2; existing.Contains(candidate); i++) candidate = baseName + " " + i.ToString(CultureInfo.InvariantCulture);
            return candidate;
        }

        public static IEnumerable<string> ViewNames(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => SafeName(v));

        /// <summary>A category by BuiltInCategory name (OST_Walls) or exact category name; a miss lists the model categories.</summary>
        public static bool TryCategory(Document doc, string name, out Category category, out CommandResult failure)
        {
            category = null;
            failure = null;
            var trimmed = (name ?? "").Trim();
            if (trimmed.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse(trimmed, true, out BuiltInCategory bic) && Enum.IsDefined(typeof(BuiltInCategory), bic))
            {
                try { category = Category.GetCategory(doc, bic); }
                catch { category = null; }
            }
            if (category == null && trimmed.Length > 0)
            {
                foreach (Category c in doc.Settings.Categories)
                {
                    if (c != null && string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        category = c;
                        break;
                    }
                }
            }
            if (category != null) return true;
            var options = new List<Category>();
            foreach (Category c in doc.Settings.Categories)
                if (c != null && c.CategoryType == CategoryType.Model && c.BuiltInCategory != BuiltInCategory.INVALID) options.Add(c);
            failure = CommandResult.Fail("CATEGORY_NOT_FOUND: no category '" + name + "'; use a BuiltInCategory name such as OST_Walls or an exact category name.",
                new JObject
                {
                    ["valid_categories"] = new JArray(options.OrderBy(c => c.BuiltInCategory.ToString()).Take(MaxOptions)
                        .Select(c => new JObject { ["builtInCategory"] = c.BuiltInCategory.ToString(), ["name"] = c.Name }))
                });
            return false;
        }

        public static List<Element> CollectInstances(Document doc, Category category, View view, int limit, out bool truncated)
        {
            truncated = false;
            var result = new List<Element>();
            var collector = view == null ? new FilteredElementCollector(doc) : new FilteredElementCollector(doc, view.Id);
            foreach (var element in collector.OfCategoryId(category.Id).WhereElementIsNotElementType())
            {
                if (result.Count >= limit)
                {
                    truncated = true;
                    break;
                }
                result.Add(element);
            }
            return result;
        }

        // ---- naming normalisation --------------------------------------------------------------------

        public static string NormalizeName(string value)
        {
            if (value == null) return "";
            var cleaned = value.Trim();
            while (cleaned.Contains("  ")) cleaned = cleaned.Replace("  ", " ");
            foreach (var ch in new[] { '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' })
                cleaned = cleaned.Replace(ch, '-');
            return cleaned;
        }

        public static string ApplyPattern(string pattern, Element element, int index)
        {
            var name = SafeName(element) ?? "";
            var result = string.IsNullOrWhiteSpace(pattern) ? name : pattern;
            result = result.Replace("{Name}", name).Replace("{Original}", name)
                .Replace("{Index}", index.ToString("000", CultureInfo.InvariantCulture))
                .Replace("{Id}", RevitCompat.GetId(element.Id).ToString(CultureInfo.InvariantCulture))
                .Replace("{Category}", SafeCategoryName(element) ?? "");
            if (element is View view) result = result.Replace("{Type}", view.ViewType.ToString());
            if (element is ViewSheet sheet) result = result.Replace("{SheetNumber}", sheet.SheetNumber ?? "");
            if (element is Level level)
                result = result.Replace("{ElevationMm}", Math.Round(Mm.FromFeet(level.Elevation), 0).ToString(CultureInfo.InvariantCulture));
            return NormalizeName(result);
        }

        // ---- parameter values (lengths mm, areas m2, volumes m3, angles degrees) ---------------------

        private static string Spec(Parameter parameter)
        {
            try
            {
                var type = parameter.Definition?.GetDataType();
                if (type == null) return null;
                if (type.Equals(SpecTypeId.Length)) return "length";
                if (type.Equals(SpecTypeId.Area)) return "area";
                if (type.Equals(SpecTypeId.Volume)) return "volume";
                if (type.Equals(SpecTypeId.Angle)) return "angle";
            }
            catch
            {
                // Parameters without a data type are read raw.
            }
            return null;
        }

        public static double FromInternal(Parameter parameter, double raw)
        {
            switch (Spec(parameter))
            {
                case "length": return Mm.FromFeet(raw, 3);
                case "area": return Math.Round(raw * SquareMetresPerSquareFoot, 4);
                case "volume": return Math.Round(raw * CubicMetresPerCubicFoot, 4);
                case "angle": return Math.Round(raw * 180.0 / Math.PI, 4);
                default: return Math.Round(raw, 6);
            }
        }

        private static double ToInternal(Parameter parameter, double value)
        {
            switch (Spec(parameter))
            {
                case "length": return Mm.ToFeet(value);
                case "area": return value / SquareMetresPerSquareFoot;
                case "volume": return value / CubicMetresPerCubicFoot;
                case "angle": return value * Math.PI / 180.0;
                default: return value;
            }
        }

        public static JToken ReadParameterValue(Document doc, Element element, string parameterName)
        {
            Parameter parameter;
            try { parameter = element?.LookupParameter(parameterName); }
            catch { parameter = null; }
            if (parameter == null) return JValue.CreateNull();
            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String: return new JValue(parameter.AsString());
                    case StorageType.Integer: return new JValue(parameter.AsInteger());
                    case StorageType.Double: return new JValue(FromInternal(parameter, parameter.AsDouble()));
                    case StorageType.ElementId:
                        var id = parameter.AsElementId();
                        return id == null ? JValue.CreateNull() : new JValue(RevitCompat.GetId(id));
                    default: return JValue.CreateNull();
                }
            }
            catch
            {
                return JValue.CreateNull();
            }
        }

        public static string AsComparableString(JToken token) =>
            token == null || token.Type == JTokenType.Null ? "" : token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None);

        public static bool TrySetParameterFromString(Parameter parameter, string value, out string error)
        {
            error = null;
            if (parameter == null) { error = "parameter not found."; return false; }
            if (parameter.IsReadOnly) { error = "parameter is read-only."; return false; }
            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        return Check(parameter.Set(value ?? ""), out error);
                    case StorageType.Integer:
                        int intValue;
                        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) intValue = 1;
                        else if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) intValue = 0;
                        else if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
                        {
                            error = "value must be an integer.";
                            return false;
                        }
                        return Check(parameter.Set(intValue), out error);
                    case StorageType.Double:
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                        {
                            error = "value must be a number (lengths mm, areas m2, volumes m3, angles degrees).";
                            return false;
                        }
                        return Check(parameter.Set(ToInternal(parameter, number)), out error);
                    case StorageType.ElementId:
                        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                        {
                            error = "value must be an element id.";
                            return false;
                        }
                        return Check(parameter.Set(RevitCompat.ToElementId(id)), out error);
                    default:
                        error = "unsupported storage type " + parameter.StorageType + ".";
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Checks that a value can be converted for the parameter, without setting it.</summary>
        public static bool CanConvert(Parameter parameter, string value, out string error)
        {
            error = null;
            switch (parameter.StorageType)
            {
                case StorageType.Integer:
                    if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                        || int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return true;
                    error = "value must be an integer.";
                    return false;
                case StorageType.Double:
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return true;
                    error = "value must be a number (lengths mm, areas m2, volumes m3, angles degrees).";
                    return false;
                case StorageType.ElementId:
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return true;
                    error = "value must be an element id.";
                    return false;
                default:
                    return true;
            }
        }

        private static bool Check(bool ok, out string error)
        {
            error = ok ? null : "Revit rejected the value.";
            return ok;
        }

        // ---- files -----------------------------------------------------------------------------------

        /// <summary>An exact .json or .csv output path through OutputFile (absolute, no overwrite unless asked).</summary>
        public static bool TryOutputPath(string path, string field, bool overwrite, out string fullPath, out CommandResult failure)
        {
            fullPath = null;
            var ext = (Path.GetExtension(path ?? "") ?? "").ToLowerInvariant();
            if (ext != ".json" && ext != ".csv")
            {
                failure = CommandResult.Fail("INVALID_PATH: " + field + " must end with .json or .csv.");
                return false;
            }
            return OutputFile.TryResolve(path, null, null, ext, overwrite, out fullPath, out failure);
        }

        public static void WriteRows(string fullPath, JArray rows, IList<string> columns)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            var text = fullPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                ? BuildCsv(rows, columns)
                : rows.ToString(Formatting.Indented);
            File.WriteAllText(fullPath, text, new UTF8Encoding(false));
        }

        public static JArray ReadRows(string path, int maxRows, long maxBytes, out string error)
        {
            error = null;
            try
            {
                var info = new FileInfo(path);
                if (info.Length > maxBytes)
                {
                    error = "IMPORT_TOO_LARGE: the import file is over " + (maxBytes / (1024 * 1024)) + " MB.";
                    return null;
                }
                var ext = (info.Extension ?? "").ToLowerInvariant();
                JArray rows;
                if (ext == ".json")
                {
                    rows = JToken.Parse(File.ReadAllText(path, Encoding.UTF8)) as JArray;
                    if (rows == null)
                    {
                        error = "INVALID_IMPORT: a JSON import file must be an array of objects.";
                        return null;
                    }
                }
                else if (ext == ".csv")
                {
                    rows = ReadCsv(path);
                }
                else
                {
                    error = "INVALID_PATH: import_path must end with .json or .csv.";
                    return null;
                }
                if (rows.Count > maxRows)
                {
                    error = "IMPORT_TOO_LARGE: the import file has " + rows.Count + " rows; at most " + maxRows + ".";
                    return null;
                }
                return rows;
            }
            catch (Exception ex)
            {
                error = "IMPORT_READ_FAILED: " + ex.Message;
                return null;
            }
        }

        private static JArray ReadCsv(string path)
        {
            var rows = new JArray();
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length == 0) return rows;
            var headers = ParseCsvLine(lines[0]);
            for (var i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var values = ParseCsvLine(lines[i]);
                var row = new JObject();
                for (var c = 0; c < headers.Count; c++) row[headers[c]] = c < values.Count ? values[c] : "";
                rows.Add(row);
            }
            return rows;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var cells = new List<string>();
            var sb = new StringBuilder();
            var inQuotes = false;
            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (ch == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else inQuotes = !inQuotes;
                }
                else if (ch == ',' && !inQuotes)
                {
                    cells.Add(sb.ToString());
                    sb.Clear();
                }
                else sb.Append(ch);
            }
            cells.Add(sb.ToString());
            return cells;
        }

        private static string BuildCsv(JArray rows, IList<string> columns)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", columns.Select(CsvEscape)));
            foreach (var row in rows.OfType<JObject>())
                sb.AppendLine(string.Join(",", columns.Select(c => CsvEscape(Cell(row[c])))));
            return sb.ToString();
        }

        private static string Cell(JToken token) =>
            token == null || token.Type == JTokenType.Null ? ""
            : token.Type == JTokenType.Float ? token.Value<double>().ToString(CultureInfo.InvariantCulture)
            : token.Type == JTokenType.String ? token.Value<string>()
            : token.ToString(Formatting.None);

        private static string CsvEscape(string value)
        {
            value = value ?? "";
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        // ---- sheet layout ----------------------------------------------------------------------------

        /// <summary>
        /// Cells of a near-square grid inside the title block frame (or a 841 x 594 mm A1 area at the sheet
        /// origin when there is none), as (centre, top-left) points in sheet feet.
        /// </summary>
        public static List<(XYZ center, XYZ topLeft)> GridCells(Document doc, ViewSheet sheet, int count, out bool usedFrame)
        {
            var frame = ViewportSheetSupport.GetSheetFrame(doc, sheet);
            usedFrame = frame.HasValue;
            double minX, minY, maxX, maxY;
            if (frame.HasValue)
            {
                minX = frame.Value.MinX; minY = frame.Value.MinY; maxX = frame.Value.MaxX; maxY = frame.Value.MaxY;
            }
            else
            {
                minX = 0; minY = 0; maxX = Mm.ToFeet(841); maxY = Mm.ToFeet(594);
            }
            var insetX = (maxX - minX) * 0.05;
            var insetY = (maxY - minY) * 0.05;
            minX += insetX; maxX -= insetX; minY += insetY; maxY -= insetY;

            var cells = new List<(XYZ, XYZ)>();
            if (count <= 0) return cells;
            var cols = (int)Math.Ceiling(Math.Sqrt(count));
            var rows = (int)Math.Ceiling(count / (double)cols);
            var w = (maxX - minX) / cols;
            var h = (maxY - minY) / rows;
            for (var i = 0; i < count; i++)
            {
                var col = i % cols;
                var row = i / cols;
                var left = minX + col * w;
                var top = maxY - row * h;
                cells.Add((new XYZ(left + w / 2, top - h / 2, 0), new XYZ(left + w * 0.05, top - h * 0.05, 0)));
            }
            return cells;
        }

        // ---- clash check (compact copy of clash_detection) -------------------------------------------

        /// <summary>
        /// Bounding-box overlap between category A and B elements, confirmed by solid intersection.
        /// maxCandidates caps the bounding-box candidate pairs that get the (slow) solid test.
        /// </summary>
        public static JObject DetectClashes(Document doc, Category a, Category b, View view,
            int maxElementsPerSide, int maxCandidates, int maxResults)
        {
            var elementsA = CollectInstances(doc, a, view, maxElementsPerSide, out var truncA);
            var elementsB = CollectInstances(doc, b, view, maxElementsPerSide, out var truncB);
            var boxesA = Boxes(elementsA, view);
            var boxesB = Boxes(elementsB, view);

            var options = new Options { DetailLevel = ViewDetailLevel.Medium, ComputeReferences = false, IncludeNonVisibleObjects = false };
            var solidCache = new Dictionary<long, List<Solid>>();
            var clashes = new JArray();
            var failed = new JArray();
            var seen = new HashSet<(long, long)>();
            int candidates = 0, solidTests = 0;
            string truncation = truncA || truncB ? "max_elements_per_category" : null;

            foreach (var ia in boxesA)
            {
                if (truncation == "max_results" || truncation == "max_pairs") break;
                foreach (var ib in boxesB)
                {
                    var idA = RevitCompat.GetId(ia.element.Id);
                    var idB = RevitCompat.GetId(ib.element.Id);
                    if (idA == idB || !Overlap(ia.box, ib.box)) continue;
                    var key = idA < idB ? (idA, idB) : (idB, idA);
                    if (!seen.Add(key)) continue; // Same category on both sides: each pair once.
                    if (clashes.Count >= maxResults) { truncation = "max_results"; break; }
                    if (candidates >= maxCandidates) { truncation = "max_pairs"; break; }
                    candidates++;

                    double volumeFt3 = 0;
                    string error = null;
                    solidTests++;
                    try
                    {
                        foreach (var sa in Solids(ia.element, options, solidCache))
                            foreach (var sb in Solids(ib.element, options, solidCache))
                            {
                                try
                                {
                                    var inter = BooleanOperationsUtils.ExecuteBooleanOperation(sa, sb, BooleanOperationsType.Intersect);
                                    if (inter != null && inter.Volume > 1e-9) volumeFt3 += inter.Volume;
                                }
                                catch (Exception ex)
                                {
                                    error = "boolean intersection failed: " + ex.Message;
                                }
                            }
                    }
                    catch (Exception ex)
                    {
                        error = "solid extraction failed: " + ex.Message;
                    }

                    if (error != null && volumeFt3 <= 0)
                    {
                        if (failed.Count < 100) failed.Add(new JObject { ["a_element_id"] = idA, ["b_element_id"] = idB, ["error"] = error });
                        continue;
                    }
                    if (volumeFt3 <= 0) continue;

                    var min = new XYZ(Math.Max(ia.box.Min.X, ib.box.Min.X), Math.Max(ia.box.Min.Y, ib.box.Min.Y), Math.Max(ia.box.Min.Z, ib.box.Min.Z));
                    var max = new XYZ(Math.Min(ia.box.Max.X, ib.box.Max.X), Math.Min(ia.box.Max.Y, ib.box.Max.Y), Math.Min(ia.box.Max.Z, ib.box.Max.Z));
                    clashes.Add(new JObject
                    {
                        ["a"] = Summary(ia.element),
                        ["b"] = Summary(ib.element),
                        ["intersection_volume_m3"] = Math.Round(volumeFt3 * CubicMetresPerCubicFoot, 6),
                        ["bbox_overlap_mm"] = new JObject
                        {
                            ["min"] = new JObject { ["x"] = Mm.FromFeet(min.X), ["y"] = Mm.FromFeet(min.Y), ["z"] = Mm.FromFeet(min.Z) },
                            ["max"] = new JObject { ["x"] = Mm.FromFeet(max.X), ["y"] = Mm.FromFeet(max.Y), ["z"] = Mm.FromFeet(max.Z) }
                        }
                    });
                }
            }

            return new JObject
            {
                ["scanned_a"] = boxesA.Count,
                ["scanned_b"] = boxesB.Count,
                ["bbox_candidates_tested"] = candidates,
                ["solid_tests"] = solidTests,
                ["returned"] = clashes.Count,
                ["truncated"] = truncation != null,
                ["truncation_reason"] = truncation,
                ["clashes"] = clashes,
                ["failed"] = failed
            };
        }

        public static XYZ ClashCenter(JObject clash)
        {
            var box = clash["bbox_overlap_mm"];
            if (box == null) return null;
            double C(string axis) => Mm.ToFeet((box["min"].Value<double>(axis) + box["max"].Value<double>(axis)) / 2.0);
            return new XYZ(C("x"), C("y"), C("z"));
        }

        public static List<long> ClashElementIds(JArray clashes)
        {
            var ids = new List<long>();
            foreach (var clash in clashes.OfType<JObject>())
                foreach (var side in new[] { "a", "b" })
                {
                    var id = clash[side]?.Value<long?>("element_id");
                    if (id.HasValue && !ids.Contains(id.Value)) ids.Add(id.Value);
                }
            return ids;
        }

        private static JObject Summary(Element element) => new JObject
        {
            ["element_id"] = RevitCompat.GetId(element.Id),
            ["category"] = SafeCategoryName(element) ?? "",
            ["name"] = SafeName(element) ?? ""
        };

        private static List<(Element element, BoundingBoxXYZ box)> Boxes(IEnumerable<Element> elements, View view)
        {
            var list = new List<(Element, BoundingBoxXYZ)>();
            foreach (var element in elements)
            {
                try
                {
                    var box = element.get_BoundingBox(view);
                    if (box != null) list.Add((element, box));
                }
                catch
                {
                    // Elements without a box cannot clash.
                }
            }
            return list;
        }

        private static bool Overlap(BoundingBoxXYZ a, BoundingBoxXYZ b) =>
            a.Min.X <= b.Max.X && a.Max.X >= b.Min.X && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y && a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;

        private static List<Solid> Solids(Element element, Options options, Dictionary<long, List<Solid>> cache)
        {
            var id = RevitCompat.GetId(element.Id);
            if (cache.TryGetValue(id, out var solids)) return solids;
            solids = new List<Solid>();
            var geometry = element.get_Geometry(options);
            if (geometry != null) CollectSolids(geometry, solids);
            cache[id] = solids;
            return solids;
        }

        private static void CollectSolids(GeometryObject obj, List<Solid> solids)
        {
            if (obj is Solid solid)
            {
                if (solid.Volume > 1e-9 && solid.Faces.Size > 0) solids.Add(solid);
            }
            else if (obj is GeometryInstance instance)
            {
                var nested = instance.GetInstanceGeometry();
                if (nested != null) foreach (GeometryObject child in nested) CollectSolids(child, solids);
            }
            else if (obj is GeometryElement element)
            {
                foreach (GeometryObject child in element) CollectSolids(child, solids);
            }
        }
    }
}
