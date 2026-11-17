// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Export/ExportDwgHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: one DWG per view at a predictable name in output_folder (file_name for a single view,
// else file_name_prefix + "sheet number - sheet name"), each exported through a staging folder and moved
// into place, so nothing else in the folder is touched and an existing file is replaced only with overwrite
// (rvt-mcp exported straight into the folder and reported files by timestamp); output_folder is created when
// missing; sheets are exported with merged views so each sheet is one file (no viewport xrefs); errors are
// failures with codes instead of success:true with an error field; no transaction is opened (DWG export
// cannot run inside one).

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Export
{
    using View = Autodesk.Revit.DB.View;

    public sealed class ExportDwgHandler : IRevitCommand
    {
        public string Name => "export_dwg";
        public string Description =>
            "Export views or sheets to AutoCAD DWG, one file per view in output_folder (absolute; created if missing). "
            + "view_ids default: the active view. file_name names the file when exporting exactly one view; otherwise files are named "
            + "file_name_prefix + 'number - name'. settings_name picks a saved DWG export setup (see list_export_settings); default settings otherwise. "
            + "Sheets export as one merged file each. Existing files are replaced only with overwrite: true.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""output_folder"":{""type"":""string""},
            ""view_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""file_name"":{""type"":""string""},
            ""file_name_prefix"":{""type"":""string""},
            ""settings_name"":{""type"":""string""},
            ""overwrite"":{""type"":""boolean""}},
            ""required"":[""output_folder""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!OptionalFunctionalityUtils.IsDWGExportAvailable()) return ExportHelpers.ExporterMissing("DWG");

            var overwrite = p.Value<bool?>("overwrite") ?? false;
            if (!ExportHelpers.TryFolder(p.Value<string>("output_folder"), out var folder, out var failure)) return failure;
            if (!ExportHelpers.TryViews(doc, p["view_ids"], out var views, out var usedActive, out failure)) return failure;
            if (!TryOptions(doc, p.Value<string>("settings_name"), out var settingsUsed, out failure)) return failure;
            if (!ExportHelpers.TryPlanTargets(folder, views, p.Value<string>("file_name"), p.Value<string>("file_name_prefix"), ".dwg",
                    overwrite, out var targets, out failure)) return failure;

            return ExportHelpers.ExportEach("dwg", views, targets, "*.dwg", overwrite, (staging, view) =>
            {
                // Fresh options per view: Revit may keep state on the options object between exports.
                TryOptions(doc, p.Value<string>("settings_name"), out _, out _, out var options);
                options.MergedViews = true;
                if (!doc.Export(staging, "export", new[] { view.Id }.ToList(), options))
                    throw new InvalidOperationException("Revit reported that the DWG export did not succeed.");
            }, new { output_folder = folder, settings_used = settingsUsed, used_active_view = usedActive });
        }

        private static bool TryOptions(Document doc, string settingsName, out string settingsUsed, out CommandResult failure) =>
            TryOptions(doc, settingsName, out settingsUsed, out failure, out _);

        private static bool TryOptions(Document doc, string settingsName, out string settingsUsed, out CommandResult failure, out DWGExportOptions options)
        {
            failure = null;
            options = null;
            settingsUsed = "<default>";
            if (string.IsNullOrEmpty(settingsName))
            {
                options = new DWGExportOptions();
                return true;
            }
            var all = new FilteredElementCollector(doc).OfClass(typeof(ExportDWGSettings)).Cast<ExportDWGSettings>().OrderBy(s => s.Name).ToList();
            var matches = all.Where(s => string.Equals(s.Name, settingsName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
            {
                failure = CommandResult.Fail((matches.Count == 0 ? "SETTINGS_NOT_FOUND: no saved DWG export setup named '" : "SETTINGS_AMBIGUOUS: several DWG export setups are named '")
                    + settingsName + "'.", new JObject
                    {
                        ["valid_settings"] = new JArray(all.Select(s => new JObject { ["id"] = RevitCompat.GetId(s.Id), ["name"] = s.Name }))
                    });
                return false;
            }
            options = matches[0].GetDWGExportOptions();
            if (options == null)
            {
                failure = CommandResult.Fail("SETTINGS_UNUSABLE: DWG export setup '" + matches[0].Name + "' did not give usable export options.");
                return false;
            }
            settingsUsed = matches[0].Name;
            return true;
        }
    }
}
