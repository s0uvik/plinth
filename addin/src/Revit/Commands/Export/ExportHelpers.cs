// Helpers shared by the export toolset's Step 8 commands. Parts replace rvt-mcp infrastructure:
// ExportTransaction (src/shared/Infrastructure/ExportTransaction.cs) and ResponsePaging, from
// rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: exports write one file per view through OutputFile's staging folder, with every target
// path planned and checked for collisions before anything is exported (rvt-mcp diffed folder timestamps).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Export
{
    using View = Autodesk.Revit.DB.View;

    internal static class ExportHelpers
    {
        public const int MaxViews = 500;
        private const int MaxOptions = 100;

        /// <summary>An absolute output folder (drive or UNC path). It is created on export when missing.</summary>
        public static bool TryFolder(string folder, out string fullFolder, out CommandResult failure)
        {
            fullFolder = null;
            failure = null;
            var absolute = !string.IsNullOrEmpty(folder)
                && ((folder.Length >= 3 && char.IsLetter(folder[0]) && folder[1] == ':' && (folder[2] == '\\' || folder[2] == '/'))
                    || folder.StartsWith(@"\\", StringComparison.Ordinal));
            if (!absolute)
            {
                failure = CommandResult.Fail("INVALID_PATH: output_folder must be an absolute path such as C:\\out.");
                return false;
            }
            try
            {
                fullFolder = Path.GetFullPath(folder);
            }
            catch (Exception ex)
            {
                failure = CommandResult.Fail("INVALID_PATH: " + ex.Message);
                return false;
            }
            if (File.Exists(fullFolder))
            {
                failure = CommandResult.Fail("INVALID_PATH: " + fullFolder + " is a file, not a folder.");
                return false;
            }
            return true;
        }

        public static bool IsBareName(string name) =>
            !string.IsNullOrWhiteSpace(name)
            && name != "." && name != ".."
            && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && name.IndexOf('/') < 0 && name.IndexOf('\\') < 0
            && name.Trim() == name
            && !name.EndsWith(".", StringComparison.Ordinal);

        /// <summary>A view or sheet name made safe for a file name (forbidden characters become '_').</summary>
        public static string SafeFileName(string raw)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = (raw ?? "").Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray();
            var name = new string(chars).Trim().TrimEnd('.');
            return name.Length == 0 ? "export" : name;
        }

        public static string DefaultName(View view) =>
            view is ViewSheet sheet ? sheet.SheetNumber + " - " + sheet.Name : view.ViewType + " - " + view.Name;

        /// <summary>Views from view_ids (templates refused, duplicates refused), or the active view when view_ids is absent.</summary>
        public static bool TryViews(Document doc, JToken ids, out List<View> views, out bool usedActiveView, out CommandResult failure)
        {
            views = new List<View>();
            usedActiveView = false;
            failure = null;
            if (ids is JArray array && array.Count > 0)
            {
                if (array.Count > MaxViews)
                {
                    failure = CommandResult.Fail("INVALID_PARAMS: at most " + MaxViews + " views per call.");
                    return false;
                }
                foreach (var token in array)
                {
                    if (!Lookup.TryView(doc, token.Value<long>(), out var view, out failure)) return false;
                    if (views.Any(v => v.Id == view.Id))
                    {
                        failure = CommandResult.Fail("INVALID_PARAMS: view " + token + " is listed twice.");
                        return false;
                    }
                    views.Add(view);
                }
                return true;
            }
            if (ids is JArray)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: view_ids is empty; omit it to export the active view.");
                return false;
            }
            var active = doc.ActiveView;
            if (active == null || active.IsTemplate)
            {
                failure = CommandResult.Fail("NO_ACTIVE_VIEW: there is no exportable active view; pass view_ids.");
                return false;
            }
            views.Add(active);
            usedActiveView = true;
            return true;
        }

        /// <summary>
        /// One target file per view: file_name when exactly one view is exported, else prefix + "number - name"
        /// for sheets or "type - name" for views. Fails before anything is written when a target exists
        /// (without overwrite) or two views would land on the same name.
        /// </summary>
        public static bool TryPlanTargets(string folder, IList<View> views, string singleName, string prefix, string extension,
            bool overwrite, out List<string> targets, out CommandResult failure)
        {
            targets = new List<string>();
            failure = null;
            if (!string.IsNullOrEmpty(singleName) && views.Count != 1)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: file_name names one file, so it needs exactly one view; use file_name_prefix for several.");
                return false;
            }
            if (!string.IsNullOrEmpty(prefix) && SafeFileName(prefix) != prefix)
            {
                failure = CommandResult.Fail("INVALID_PATH: file_name_prefix must not contain folders or characters Windows forbids.");
                return false;
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var view in views)
            {
                string name;
                if (!string.IsNullOrEmpty(singleName))
                {
                    if (!IsBareName(singleName))
                    {
                        failure = CommandResult.Fail("INVALID_PATH: the file name must be a plain name without folders, '..' or characters Windows forbids.");
                        return false;
                    }
                    name = singleName;
                }
                else
                {
                    name = SafeFileName((prefix ?? "") + DefaultName(view));
                }
                if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name += extension;
                if (!seen.Add(name))
                {
                    name = Path.GetFileNameWithoutExtension(name) + " (" + RevitCompat.GetId(view.Id) + ")" + extension;
                    seen.Add(name);
                }
                targets.Add(Path.Combine(folder, name));
            }
            var existing = targets.Where(File.Exists).ToList();
            if (existing.Count > 0 && !overwrite)
            {
                failure = CommandResult.Fail("FILE_EXISTS: " + existing.Count + " target file(s) already exist; pass overwrite: true to replace them. Nothing was exported.",
                    new JObject { ["existing_files"] = new JArray(existing.Take(MaxOptions)) });
                return false;
            }
            var folders = targets.Where(Directory.Exists).ToList();
            if (folders.Count > 0)
            {
                failure = CommandResult.Fail("INVALID_PATH: " + folders[0] + " is a folder.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Exports each view on its own into a private staging folder, then moves the single produced file
        /// (matching pattern) to its planned target. One view failing does not stop the others.
        /// </summary>
        public static CommandResult ExportEach(string format, IList<View> views, IList<string> targets, string pattern, bool overwrite,
            Action<string, View> exportOne, object extra = null)
        {
            var written = new JArray();
            var failed = new JArray();
            for (var i = 0; i < views.Count; i++)
            {
                var view = views[i];
                string staging = null;
                try
                {
                    staging = OutputFile.NewStagingFolder(targets[i]);
                    exportOne(staging, view);
                    OutputFile.Deliver(staging, pattern, targets[i], overwrite);
                    written.Add(new JObject
                    {
                        ["path"] = targets[i],
                        ["size_bytes"] = new FileInfo(targets[i]).Length,
                        ["view_id"] = RevitCompat.GetId(view.Id),
                        ["view_name"] = view.Name,
                        ["sheet_number"] = (view as ViewSheet)?.SheetNumber
                    });
                }
                catch (Exception ex)
                {
                    failed.Add(new JObject
                    {
                        ["view_id"] = RevitCompat.GetId(view.Id),
                        ["view_name"] = view.Name,
                        ["target"] = targets[i],
                        ["error"] = ex.Message
                    });
                }
                finally
                {
                    OutputFile.Cleanup(staging);
                }
            }

            var data = new JObject
            {
                ["format"] = format,
                ["file_count"] = written.Count,
                ["failedCount"] = failed.Count,
                ["files"] = written,
                ["failed"] = failed
            };
            if (extra != null) data.Merge(JObject.FromObject(extra));
            if (written.Count == 0)
                return CommandResult.Fail("EXPORT_FAILED: no " + format + " file was written. " + (failed.Count > 0 ? failed[0].Value<string>("error") : ""), data);
            return CommandResult.Ok(data);
        }

        public const string OutputSchema = @"""output_path"":{""type"":""string""},""output_folder"":{""type"":""string""},""file_name"":{""type"":""string""},""overwrite"":{""type"":""boolean""}";

        /// <summary>
        /// One file at an exact path: Revit exports into a staging folder (always as "export"), then the single
        /// produced file matching pattern is moved to target. Returns the path and size merged with extra.
        /// </summary>
        public static CommandResult ExportSingle(string format, string target, string pattern, bool overwrite, Action<string> export, object extra = null)
        {
            string staging = null;
            try
            {
                staging = OutputFile.NewStagingFolder(target);
                export(staging);
                OutputFile.Deliver(staging, pattern, target, overwrite);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("EXPORT_FAILED: " + format + " export failed: " + ex.Message + " Nothing was written to the target path.");
            }
            finally
            {
                OutputFile.Cleanup(staging);
            }
            var data = new JObject
            {
                ["format"] = format,
                ["path"] = target,
                ["size_bytes"] = new FileInfo(target).Length
            };
            if (extra != null) data.Merge(JObject.FromObject(extra));
            return CommandResult.Ok(data);
        }

        /// <summary>
        /// For exporters that need a modifiable document (IFC, DWF/DWFx, gbXML on 2026+): runs the export in a
        /// transaction that is always rolled back, so the file is written and the model is left unchanged.
        /// (Replaces rvt-mcp's ExportTransaction.)
        /// </summary>
        public static T RunRolledBack<T>(Document doc, string name, Func<T> export)
        {
            if (doc.IsModifiable) return export();
            using (var tx = new Transaction(doc, name))
            {
                tx.Start();
                try
                {
                    return export();
                }
                finally
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                }
            }
        }

        public static CommandResult ExporterMissing(string what) =>
            CommandResult.Fail("EXPORTER_NOT_AVAILABLE: the " + what + " exporter is not installed or not enabled in this Revit session. Nothing was written.");

        /// <summary>start_index (default 0) and max_results (default and maximum given).</summary>
        public static bool TryPaging(JObject p, int defaultMax, int hardMax, out int start, out int max, out CommandResult failure)
        {
            failure = null;
            start = p.Value<int?>("start_index") ?? 0;
            max = p.Value<int?>("max_results") ?? defaultMax;
            if (start < 0) failure = CommandResult.Fail("INVALID_PARAMS: start_index must be 0 or more.");
            else if (max < 1 || max > hardMax) failure = CommandResult.Fail("INVALID_PARAMS: max_results must be between 1 and " + hardMax + ".");
            return failure == null;
        }

        public const string PagingSchema = @"""start_index"":{""type"":""integer""},""max_results"":{""type"":""integer""}";

        /// <summary>A category by built-in name (OST_Walls) or by its display name in this document.</summary>
        public static bool TryCategory(Document doc, string name, out Category category, out CommandResult failure)
        {
            failure = null;
            category = null;
            if (!string.IsNullOrEmpty(name) && name.StartsWith("OST_", StringComparison.OrdinalIgnoreCase))
            {
                if (Enum.TryParse(name, true, out BuiltInCategory bic) && Enum.IsDefined(typeof(BuiltInCategory), bic))
                {
                    try { category = Category.GetCategory(doc, bic); }
                    catch { category = null; }
                }
            }
            else
            {
                foreach (Category c in doc.Settings.Categories)
                {
                    if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        category = c;
                        break;
                    }
                }
            }
            if (category != null) return true;
            var valid = new List<string>();
            foreach (Category c in doc.Settings.Categories)
                if (c.CategoryType == CategoryType.Model || c.CategoryType == CategoryType.Annotation)
                    valid.Add(c.Name);
            failure = CommandResult.Fail("CATEGORY_NOT_FOUND: no category '" + name + "'; pass a built-in name such as OST_Walls or a category name from valid_categories.",
                new JObject { ["valid_categories"] = new JArray(valid.OrderBy(n => n).Take(MaxOptions * 3)) });
            return false;
        }
    }
}
