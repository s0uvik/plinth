// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Workflows/WorkflowDataRoundtripHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: export_path goes through Export/OutputFile (absolute, exact, no overwrite unless overwrite: true)
// and is required only when exporting (rvt-mcp required it for import_only too); key_field is element_id or
// unique_id; values use Plinth units (lengths mm, areas m2, volumes m3, angles degrees) through Mm and the Plinth factors;
// the category accepts BuiltInCategory names and a miss lists the valid ones; the import file is capped at 20 MB
// and 10000 rows, and the response lists at most 500 changes, 200 skipped rows and 200 errors (with totals);
// rvt-mcp's output=file NDJSON report spilling is dropped (no response spill infrastructure in Plinth); the import
// transaction is "Plinth: ..." and any Revit warning rolls it back (FailureScope). dry_run applies to the model
// import only; the export file is written whenever the mode exports.
// Duplicate before edit (TypeEditGuard): the export lists placed elements only, but an import row keyed to a type (or a
// material) would change something shared. Such rows need duplicate_as (the values go to a new copy; no element moves)
// or confirm_edit_all (= its exact user count); otherwise the import is refused with TYPE_IN_USE / MATERIAL_IN_USE,
// in dry runs too, before anything changes.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Workflows
{
    public sealed class WorkflowDataRoundtripHandler : IRevitCommand
    {
        private const int MaxExportRows = 10000;
        private const int MaxImportRows = 10000;
        private const long MaxImportBytes = 20L * 1024 * 1024;
        private const int MaxListed = 500;
        private const int MaxErrors = 200;

        private static readonly string[] Metadata = { "element_id", "id", "unique_id", "category", "name", "type_name", "level_name" };

        public string Name => "workflow_data_roundtrip";
        public string Description =>
            "Export a category's parameter values to .json or .csv (export_path, exact; overwrite to replace) and/or import edited values back "
            + "(import_path). mode: export_only (default), import_only, export_then_import. key_field: element_id or unique_id. "
            + "Units: lengths mm, areas m2, volumes m3, angles degrees. dry_run (default true) validates the import without changing the model. "
            + "Rows keyed to a type or material change something shared:" + TypeEditGuard.Rule
            + " Several in one import: duplicate_as {\"<id>\": \"name\"} and confirm_edit_all {\"<id>\": N}.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""category"":{""type"":""string""},
            ""parameter_names"":{""type"":""array"",""items"":{""type"":""string""}},
            ""export_path"":{""type"":""string""},
            ""import_path"":{""type"":""string""},
            ""mode"":{""type"":""string"",""enum"":[""export_only"",""import_only"",""export_then_import""]},
            ""dry_run"":{""type"":""boolean""},
            ""key_field"":{""type"":""string"",""enum"":[""element_id"",""unique_id""]},
            ""overwrite"":{""type"":""boolean""},
            " + TypeEditGuard.SchemaFields + @"},
            ""required"":[""category""]}";
        public bool ReadOnly => false;

        private sealed class Change
        {
            public long ElementId;
            public Element Element;
            public Parameter Parameter;
            public string ParameterName;
            public string Value;
        }

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var mode = p.Value<string>("mode") ?? "export_only";
            var dryRun = p.Value<bool?>("dry_run") ?? true;
            var keyField = p.Value<string>("key_field") ?? "element_id";
            var overwrite = p.Value<bool?>("overwrite") ?? false;
            var parameterNames = (p["parameter_names"]?.ToObject<string[]>() ?? new string[0])
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (!WorkflowsHelpers.TryCategory(doc, p.Value<string>("category"), out var category, out var failure)) return failure;

            var exports = mode != "import_only";
            var imports = mode != "export_only";
            string exportPath = null;
            if (exports)
            {
                if (string.IsNullOrEmpty(p.Value<string>("export_path")))
                    return CommandResult.Fail("INVALID_PARAMS: mode " + mode + " needs export_path (an absolute .json or .csv path).");
                if (!WorkflowsHelpers.TryOutputPath(p.Value<string>("export_path"), "export_path", overwrite, out exportPath, out failure)) return failure;
            }
            else if (p["export_path"] != null)
                return CommandResult.Fail("INVALID_PARAMS: export_path is not used with mode import_only.");

            string importPath = null;
            if (imports)
            {
                importPath = p.Value<string>("import_path");
                if (string.IsNullOrEmpty(importPath) || !Path.IsPathRooted(importPath))
                    return CommandResult.Fail("INVALID_PARAMS: mode " + mode + " needs import_path, an absolute path to a .json or .csv file.");
                if (!File.Exists(importPath) && mode == "import_only")
                    return CommandResult.Fail("FILE_NOT_FOUND: " + importPath + " does not exist.");
            }
            else if (p["import_path"] != null)
                return CommandResult.Fail("INVALID_PARAMS: import_path is not used with mode export_only.");

            var steps = new JArray();
            var warnings = new List<string>();
            var changed = new JArray();
            var skipped = new JArray();
            var errors = new JArray();
            var modifiedIds = new List<long>();
            int exportedRows = 0, importedRows = 0, changeCount = 0, skippedCount = 0, errorCount = 0;

            if (exports)
            {
                try
                {
                    var rows = ExportRows(doc, category, parameterNames, out var columns, out var truncated);
                    WorkflowsHelpers.WriteRows(exportPath, rows, columns);
                    exportedRows = rows.Count;
                    if (truncated) warnings.Add("The export stopped at " + MaxExportRows + " elements.");
                    steps.Add(WorkflowsHelpers.Step("Export Element Parameters", "file write", "succeeded",
                        "Export '" + category.Name + "' to " + exportPath, new { output_path = exportPath, rows = rows.Count, columns = columns.Count, truncated }));
                }
                catch (Exception ex)
                {
                    steps.Add(WorkflowsHelpers.Step("Export Element Parameters", "file write", "failed", "Export category data.", null, ex.Message));
                    return CommandResult.Ok(Result("failed", dryRun, steps, warnings, modifiedIds, exportPath, exportedRows, 0,
                        changed, 0, skipped, 0, errors, 0, WorkflowsHelpers.Rollback("None", false, "The export failed; the model was not touched.")));
                }
            }

            if (imports)
            {
                var rows = WorkflowsHelpers.ReadRows(importPath, MaxImportRows, MaxImportBytes, out var readError);
                if (rows == null)
                {
                    steps.Add(WorkflowsHelpers.Step("Read Import File", "file read", "failed", "Read " + importPath, null, readError));
                    errors.Add(readError);
                    return CommandResult.Ok(Result("failed", dryRun, steps, warnings, modifiedIds, exportPath, exportedRows, 0,
                        changed, 0, skipped, 0, errors, 1, WorkflowsHelpers.Rollback("None", false, "The import file could not be read; the model was not touched.")));
                }
                importedRows = rows.Count;
                steps.Add(WorkflowsHelpers.Step("Read Import File", "file read", "succeeded", "Read " + importPath, new { rows = rows.Count }));

                var changes = Validate(doc, category, rows, parameterNames, keyField, changed, skipped, errors, ref skippedCount, ref errorCount);
                changeCount = changes.Count;
                steps.Add(WorkflowsHelpers.Step("Validate Import Changes", "parameter checks", errorCount == 0 ? "succeeded" : "failed",
                    "Check keys, duplicates, writable parameters and value conversion.",
                    new { proposed_changes = changes.Count, skipped_rows = skippedCount, validation_errors = errorCount }));
                if (errorCount > 0)
                    return CommandResult.Ok(Result("failed", dryRun, steps, warnings, modifiedIds, exportPath, exportedRows, importedRows,
                        changed, changeCount, skipped, skippedCount, errors, errorCount,
                        WorkflowsHelpers.Rollback("None", false, "Validation failed; the model was not touched.")));

                // Rows that change a type or material are shared edits: refuse before anything changes unless the caller chose.
                if (!TypeEditGuard.TryPlan(doc, p, changes.Where(c => TypeEditGuard.IsShared(c.Element)).Select(c => new SharedEdit(c.Element)),
                        out var plan, out var refusal))
                    return refusal;

                if (dryRun)
                {
                    steps.Add(WorkflowsHelpers.Step("Apply Import Changes", "Parameter.Set", "skipped", "Dry run: validated changes were not applied.", new { proposed_changes = changes.Count }));
                    return CommandResult.Ok(Result("succeeded", true, steps, warnings, modifiedIds, exportPath, exportedRows, importedRows,
                        changed, changeCount, skipped, skippedCount, errors, errorCount,
                        WorkflowsHelpers.Rollback("Transaction", false, "Dry run; no transaction opened.")));
                }

                if (changes.Count > 0)
                {
                    using (var tx = new Transaction(doc, "Plinth: Workflow data roundtrip import"))
                    {
                        tx.Start();
                        try
                        {
                            plan.Apply(doc);
                            foreach (var change in changes)
                            {
                                var target = plan.TargetFor(change.Element);
                                var parameter = target.Id == change.Element.Id ? change.Parameter : target.LookupParameter(change.ParameterName);
                                if (parameter == null || parameter.IsReadOnly)
                                    throw new InvalidOperationException("the copy of element " + change.ElementId + " has no writable parameter '" + change.ParameterName + "'");
                                if (!WorkflowsHelpers.TrySetParameterFromString(parameter, change.Value, out var setError))
                                    throw new InvalidOperationException("element " + change.ElementId + " parameter '" + change.ParameterName + "': " + setError);
                                modifiedIds.Add(change.ElementId);
                            }
                            if (tx.Commit() != TransactionStatus.Committed)
                                throw new InvalidOperationException("Revit rolled the import back; see failures.");
                            steps.Add(WorkflowsHelpers.Step("Apply Import Changes", "Parameter.Set", "succeeded", "Applied the validated changes.",
                                new { changed_values = changes.Count, changed_elements = modifiedIds.Distinct().Count(), shared_edits = plan.Report() }));
                        }
                        catch (Exception ex)
                        {
                            if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                            errors.Add(ex.Message);
                            steps.Add(WorkflowsHelpers.Step("Apply Import Changes", "Parameter.Set", "failed", "Apply the validated changes.", null, ex.Message));
                            return CommandResult.Ok(Result("failed", false, steps, warnings, new List<long>(), exportPath, exportedRows, importedRows,
                                changed, changeCount, skipped, skippedCount, errors, errorCount + 1,
                                WorkflowsHelpers.Rollback("Transaction", true, "The import failed and was rolled back; nothing was changed.")));
                        }
                    }
                }
                else
                {
                    steps.Add(WorkflowsHelpers.Step("Apply Import Changes", "Parameter.Set", "skipped", "No values differ from the model.", null));
                }
            }

            return CommandResult.Ok(Result("succeeded", dryRun, steps, warnings, modifiedIds, exportPath, exportedRows, importedRows,
                changed, changeCount, skipped, skippedCount, errors, errorCount,
                WorkflowsHelpers.Rollback(imports ? "Transaction" : "None", false, imports ? "Import committed." : "Export only; the model was not touched.")));
        }

        private JObject Result(string status, bool dryRun, JArray steps, IEnumerable<string> warnings, List<long> modifiedIds, string exportPath,
            int exportedRows, int importedRows, JArray changed, int changedCount, JArray skipped, int skippedCount, JArray errors, int errorCount, JObject rollback)
        {
            var envelope = WorkflowsHelpers.Envelope(Name, dryRun, status, steps, new long[0], modifiedIds, warnings, rollback);
            envelope["exported_file_path"] = exportPath;
            envelope["exported_rows"] = exportedRows;
            envelope["imported_row_count"] = importedRows;
            envelope["changed_count"] = changedCount;
            envelope["changed_elements"] = changed;
            envelope["skipped_count"] = skippedCount;
            envelope["skipped_rows"] = skipped;
            envelope["validation_error_count"] = errorCount;
            envelope["validation_errors"] = errors;
            envelope["truncated_lists"] = changedCount > changed.Count || skippedCount > skipped.Count || errorCount > errors.Count;
            return envelope;
        }

        private static JArray ExportRows(Document doc, Category category, string[] parameterNames, out List<string> columns, out bool truncated)
        {
            var elements = WorkflowsHelpers.CollectInstances(doc, category, null, MaxExportRows, out truncated);
            var parameters = parameterNames.Length > 0 ? parameterNames : new[] { "Mark", "Comments" };
            columns = new List<string> { "element_id", "unique_id", "category", "name", "type_name", "level_name" };
            foreach (var name in parameters)
                if (!columns.Contains(name, StringComparer.OrdinalIgnoreCase)) columns.Add(name);

            var rows = new JArray();
            foreach (var element in elements)
            {
                var type = element.GetTypeId() == ElementId.InvalidElementId ? null : doc.GetElement(element.GetTypeId());
                var level = element.LevelId == ElementId.InvalidElementId ? null : doc.GetElement(element.LevelId);
                var row = new JObject
                {
                    ["element_id"] = RevitCompat.GetId(element.Id),
                    ["unique_id"] = element.UniqueId,
                    ["category"] = WorkflowsHelpers.SafeCategoryName(element),
                    ["name"] = WorkflowsHelpers.SafeName(element),
                    ["type_name"] = WorkflowsHelpers.SafeName(type),
                    ["level_name"] = WorkflowsHelpers.SafeName(level)
                };
                foreach (var name in parameters) row[name] = WorkflowsHelpers.ReadParameterValue(doc, element, name);
                rows.Add(row);
            }
            return rows;
        }

        private static List<Change> Validate(Document doc, Category category, JArray rows, string[] requested, string keyField,
            JArray changed, JArray skipped, JArray errors, ref int skippedCount, ref int errorCount)
        {
            var changes = new List<Change>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < rows.Count; i++)
            {
                if (!(rows[i] is JObject row))
                {
                    if (skipped.Count < MaxErrors) skipped.Add(new JObject { ["row"] = i, ["reason"] = "the row is not an object." });
                    skippedCount++;
                    continue;
                }
                var keyToken = row[keyField] ?? (keyField == "element_id" ? row["id"] : null);
                var key = WorkflowsHelpers.AsComparableString(keyToken).Trim();
                if (key.Length == 0)
                {
                    AddError(errors, ref errorCount, "row " + i + ": missing " + keyField + ".");
                    continue;
                }
                if (!seen.Add(key))
                {
                    AddError(errors, ref errorCount, "row " + i + ": duplicate key " + key + ".");
                    continue;
                }

                Element element = null;
                if (keyField == "unique_id") element = doc.GetElement(key);
                else if (long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawId)) element = doc.GetElement(RevitCompat.ToElementId(rawId));
                if (element == null || element.Category == null || element.Category.Id != category.Id)
                {
                    if (skipped.Count < MaxErrors)
                        skipped.Add(new JObject { ["row"] = i, ["key"] = key, ["reason"] = element == null ? "element not found." : "element is not in category '" + category.Name + "'." });
                    skippedCount++;
                    continue;
                }

                var names = requested.Length > 0
                    ? requested
                    : row.Properties().Select(x => x.Name).Where(n => !Metadata.Contains(n, StringComparer.OrdinalIgnoreCase)).ToArray();
                var elementId = RevitCompat.GetId(element.Id);
                foreach (var name in names)
                {
                    if (!row.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var valueToken)) continue;
                    var parameter = element.LookupParameter(name);
                    if (parameter == null)
                    {
                        AddError(errors, ref errorCount, "row " + i + " element " + elementId + ": no parameter '" + name + "'.");
                        continue;
                    }
                    if (parameter.IsReadOnly)
                    {
                        AddError(errors, ref errorCount, "row " + i + " element " + elementId + ": parameter '" + name + "' is read-only.");
                        continue;
                    }
                    var oldValue = WorkflowsHelpers.ReadParameterValue(doc, element, name);
                    var newValue = WorkflowsHelpers.AsComparableString(valueToken);
                    if (string.Equals(WorkflowsHelpers.AsComparableString(oldValue), newValue, StringComparison.Ordinal)) continue;
                    if (!WorkflowsHelpers.CanConvert(parameter, newValue, out var convertError))
                    {
                        AddError(errors, ref errorCount, "row " + i + " element " + elementId + " '" + name + "': " + convertError);
                        continue;
                    }
                    if (parameter.StorageType == StorageType.Double
                        && double.TryParse(newValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                        && oldValue.Type != JTokenType.Null && Math.Abs(oldValue.Value<double>() - n) < 1e-9) continue;

                    changes.Add(new Change { ElementId = elementId, Element = element, Parameter = parameter, ParameterName = name, Value = newValue });
                    if (changed.Count < MaxListed)
                        changed.Add(new JObject
                        {
                            ["row"] = i,
                            ["element_id"] = elementId,
                            ["parameter"] = name,
                            ["old_value"] = oldValue,
                            ["new_value"] = newValue
                        });
                }
            }
            return changes;
        }

        private static void AddError(JArray errors, ref int count, string message)
        {
            if (errors.Count < MaxErrors) errors.Add(message);
            count++;
        }
    }
}
