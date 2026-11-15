// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Export/ExportIfcHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: written to the exact path asked for (output_path, or output_folder + file_name) through a
// staging folder; an existing file is replaced only with overwrite; output_folder is created when missing; an
// unavailable IFC exporter fails with EXPORTER_NOT_AVAILABLE; a requested IFC version that cannot be set fails
// instead of silently falling back to the default; optional view_id limits the export to what that view shows.
// rvt-mcp's ExportTransaction is replaced by ExportHelpers.RunRolledBack (the IFC exporter needs a modifiable
// document; the transaction is always rolled back, so the model is unchanged).

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Export
{
    public sealed class ExportIfcHandler : IRevitCommand
    {
        public string Name => "export_ifc";
        public string Description =>
            "Export the model to one IFC file at output_path, or output_folder + file_name (absolute; .ifc added; folder created if missing). "
            + "ifc_version: IFC2x3, IFC4 or default (the exporter's default). view_id: optional, export only what that view shows. "
            + "An existing file is replaced only with overwrite: true. The model is not changed.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + ExportHelpers.OutputSchema + @",
            ""ifc_version"":{""type"":""string"",""enum"":[""IFC2x3"",""IFC4"",""default""]},
            ""view_id"":{""type"":""integer""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!OptionalFunctionalityUtils.IsIFCAvailable()) return ExportHelpers.ExporterMissing("IFC");
            if (doc.IsReadOnly) return CommandResult.Fail("DOCUMENT_READ_ONLY: IFC export needs a modifiable document.");

            var overwrite = p.Value<bool?>("overwrite") ?? false;
            if (!OutputFile.TryResolve(p.Value<string>("output_path"), p.Value<string>("output_folder"), p.Value<string>("file_name"), ".ifc",
                    overwrite, out var target, out var failure)) return failure;

            var version = p.Value<string>("ifc_version") ?? "default";
            var options = new IFCExportOptions();
            try
            {
                if (version == "IFC2x3") options.FileVersion = IFCVersion.IFC2x3;
                else if (version == "IFC4") options.FileVersion = IFCVersion.IFC4;
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("IFC_VERSION_UNAVAILABLE: this Revit cannot export " + version + ": " + ex.Message + " Try ifc_version: default.");
            }

            long? viewId = null;
            if (p["view_id"] != null)
            {
                if (!Lookup.TryView(doc, p.Value<long>("view_id"), out var view, out failure)) return failure;
                options.FilterViewId = view.Id;
                viewId = RevitCompat.GetId(view.Id);
            }

            return ExportHelpers.ExportSingle("ifc", target, "*.ifc", overwrite, staging =>
            {
                var ok = ExportHelpers.RunRolledBack(doc, "B2M: Export IFC (rolled back)", () => doc.Export(staging, "export", options));
                if (!ok) throw new InvalidOperationException("Revit reported that the IFC export did not succeed.");
            }, new { ifc_version = version, view_id = viewId });
        }
    }
}
