// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Export/ExportDwfHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: either one DWF/DWFx per view (file_name for a single view, else file_name_prefix +
// "number - name") or, with combine, one multi-page file named file_name; every file goes through a staging
// folder and is moved into place, an existing file is replaced only with overwrite, output_folder is created
// when missing. rvt-mcp's ExportTransaction is replaced by ExportHelpers.RunRolledBack (the DWF exporter needs
// a modifiable document; the transaction is always rolled back, so the model is unchanged).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Export
{
    using View = Autodesk.Revit.DB.View;

    public sealed class ExportDwfHandler : IRevitCommand
    {
        public string Name => "export_dwf";
        public string Description =>
            "Export views or sheets to Autodesk DWF (or DWFx with use_dwfx) in output_folder (absolute; created if missing). "
            + "view_ids default: the active view. Without combine: one file per view, named file_name (one view) or file_name_prefix + 'number - name'. "
            + "With combine: true, all views go into one multi-page file named file_name (required). Existing files are replaced only with overwrite: true.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""output_folder"":{""type"":""string""},
            ""view_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""file_name"":{""type"":""string""},
            ""file_name_prefix"":{""type"":""string""},
            ""use_dwfx"":{""type"":""boolean""},
            ""combine"":{""type"":""boolean""},
            ""overwrite"":{""type"":""boolean""}},
            ""required"":[""output_folder""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!OptionalFunctionalityUtils.IsDWFExportAvailable()) return ExportHelpers.ExporterMissing("DWF");
            if (doc.IsReadOnly) return CommandResult.Fail("DOCUMENT_READ_ONLY: DWF export needs a modifiable document.");

            var overwrite = p.Value<bool?>("overwrite") ?? false;
            var dwfx = p.Value<bool?>("use_dwfx") ?? false;
            var combine = p.Value<bool?>("combine") ?? false;
            var extension = dwfx ? ".dwfx" : ".dwf";
            var format = dwfx ? "dwfx" : "dwf";
            if (!ExportHelpers.TryFolder(p.Value<string>("output_folder"), out var folder, out var failure)) return failure;
            if (!ExportHelpers.TryViews(doc, p["view_ids"], out var views, out var usedActive, out failure)) return failure;

            if (!combine)
            {
                if (!ExportHelpers.TryPlanTargets(folder, views, p.Value<string>("file_name"), p.Value<string>("file_name_prefix"), extension,
                        overwrite, out var targets, out failure)) return failure;
                return ExportHelpers.ExportEach(format, views, targets, "*" + extension, overwrite,
                    (staging, view) => Export(doc, staging, new List<View> { view }, dwfx),
                    new { output_folder = folder, combined = false, used_active_view = usedActive });
            }

            if (!string.IsNullOrEmpty(p.Value<string>("file_name_prefix")))
                return CommandResult.Fail("INVALID_PARAMS: file_name_prefix is for one file per view; with combine pass file_name.");
            if (string.IsNullOrEmpty(p.Value<string>("file_name")))
                return CommandResult.Fail("INVALID_PARAMS: combine needs file_name for the one combined file.");
            if (!OutputFile.TryResolve(null, folder, p.Value<string>("file_name"), extension, overwrite, out var target, out failure)) return failure;

            string stagingFolder = null;
            try
            {
                stagingFolder = OutputFile.NewStagingFolder(target);
                Export(doc, stagingFolder, views, dwfx);
                OutputFile.Deliver(stagingFolder, "*" + extension, target, overwrite);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("EXPORT_FAILED: " + format.ToUpperInvariant() + " export failed: " + ex.Message);
            }
            finally
            {
                OutputFile.Cleanup(stagingFolder);
            }
            return CommandResult.Ok(new
            {
                format,
                combined = true,
                path = target,
                size_bytes = new FileInfo(target).Length,
                used_active_view = usedActive,
                views = views.Select(v => new { id = RevitCompat.GetId(v.Id), name = v.Name, sheet_number = (v as ViewSheet)?.SheetNumber })
            });
        }

        private static void Export(Document doc, string folder, IList<View> views, bool dwfx)
        {
            var set = new ViewSet();
            foreach (var view in views) set.Insert(view);
            bool ok;
            if (dwfx)
            {
                var options = new DWFXExportOptions { MergedViews = true };
                ok = ExportHelpers.RunRolledBack(doc, "B2M: Export DWFx (rolled back)", () => doc.Export(folder, "export", set, options));
            }
            else
            {
                var options = new DWFExportOptions { MergedViews = true };
                ok = ExportHelpers.RunRolledBack(doc, "B2M: Export DWF (rolled back)", () => doc.Export(folder, "export", set, options));
            }
            if (!ok) throw new InvalidOperationException("Revit reported that the export did not succeed.");
        }
    }
}
