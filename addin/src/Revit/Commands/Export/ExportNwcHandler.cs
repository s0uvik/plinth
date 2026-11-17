// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Export/ExportNwcHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: written to the exact path asked for (output_path, or output_folder + file_name) through a
// staging folder; an existing file is replaced only with overwrite; output_folder is created when missing; a
// missing Navisworks exporter fails with EXPORTER_NOT_AVAILABLE before anything runs; the scope view is
// checked with the shared view lookup. No transaction is opened.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Export
{
    public sealed class ExportNwcHandler : IRevitCommand
    {
        public string Name => "export_nwc";
        public string Description =>
            "Export the model to one Navisworks NWC file at output_path, or output_folder + file_name (absolute; .nwc added; folder created if missing). "
            + "export_scope_view_id: optional view whose visible elements are exported; default the whole model. "
            + "Needs the Navisworks NWC exporter installed. An existing file is replaced only with overwrite: true.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + ExportHelpers.OutputSchema + @",
            ""export_scope_view_id"":{""type"":""integer""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!OptionalFunctionalityUtils.IsNavisworksExporterAvailable()) return ExportHelpers.ExporterMissing("Navisworks NWC");

            var overwrite = p.Value<bool?>("overwrite") ?? false;
            if (!OutputFile.TryResolve(p.Value<string>("output_path"), p.Value<string>("output_folder"), p.Value<string>("file_name"), ".nwc",
                    overwrite, out var target, out var failure)) return failure;

            var options = new NavisworksExportOptions { ExportScope = NavisworksExportScope.Model };
            long? scopeViewId = null;
            if (p["export_scope_view_id"] != null)
            {
                if (!Lookup.TryView(doc, p.Value<long>("export_scope_view_id"), out var view, out failure)) return failure;
                options.ExportScope = NavisworksExportScope.View;
                options.ViewId = view.Id;
                scopeViewId = RevitCompat.GetId(view.Id);
            }

            return ExportHelpers.ExportSingle("nwc", target, "*.nwc", overwrite,
                staging => doc.Export(staging, "export", options),
                new { scope = scopeViewId.HasValue ? "view" : "model", export_scope_view_id = scopeViewId });
        }
    }
}
