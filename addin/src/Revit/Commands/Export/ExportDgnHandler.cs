// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Export/ExportDgnHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: one DGN per view at a predictable name in output_folder (file_name for a single view,
// else file_name_prefix + "number - name"), exported through a staging folder and moved into place; an
// existing file is replaced only with overwrite; output_folder is created when missing; seed_file must be an
// absolute path to an existing .dgn (rvt-mcp passed any string through); sheets export merged; errors are
// coded failures; no transaction is opened.

using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Export
{
    public sealed class ExportDgnHandler : IRevitCommand
    {
        public string Name => "export_dgn";
        public string Description =>
            "Export views or sheets to MicroStation DGN, one file per view in output_folder (absolute; created if missing). "
            + "view_ids default: the active view. file_name names the file for exactly one view; otherwise file_name_prefix + 'number - name'. "
            + "seed_file: optional absolute path of an existing .dgn seed. Existing files are replaced only with overwrite: true.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""output_folder"":{""type"":""string""},
            ""view_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""file_name"":{""type"":""string""},
            ""file_name_prefix"":{""type"":""string""},
            ""seed_file"":{""type"":""string""},
            ""overwrite"":{""type"":""boolean""}},
            ""required"":[""output_folder""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!OptionalFunctionalityUtils.IsDGNExportAvailable()) return ExportHelpers.ExporterMissing("DGN");

            var overwrite = p.Value<bool?>("overwrite") ?? false;
            var seed = p.Value<string>("seed_file");
            if (!string.IsNullOrEmpty(seed))
            {
                if (!Path.IsPathRooted(seed) || !seed.EndsWith(".dgn", StringComparison.OrdinalIgnoreCase) || !File.Exists(seed))
                    return CommandResult.Fail("INVALID_PATH: seed_file must be the absolute path of an existing .dgn file.");
            }
            if (!ExportHelpers.TryFolder(p.Value<string>("output_folder"), out var folder, out var failure)) return failure;
            if (!ExportHelpers.TryViews(doc, p["view_ids"], out var views, out var usedActive, out failure)) return failure;
            if (!ExportHelpers.TryPlanTargets(folder, views, p.Value<string>("file_name"), p.Value<string>("file_name_prefix"), ".dgn",
                    overwrite, out var targets, out failure)) return failure;

            return ExportHelpers.ExportEach("dgn", views, targets, "*.dgn", overwrite, (staging, view) =>
            {
                using (var options = new DGNExportOptions())
                {
                    options.MergedViews = true;
                    if (!string.IsNullOrEmpty(seed)) options.SeedName = seed;
                    if (!doc.Export(staging, "export", new[] { view.Id }.ToList(), options))
                        throw new InvalidOperationException("Revit reported that the DGN export did not succeed; a DGN seed file may be required (seed_file).");
                }
            }, new { output_folder = folder, seed_file = seed, used_active_view = usedActive });
        }
    }
}
