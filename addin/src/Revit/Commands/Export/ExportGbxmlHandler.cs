// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Export/ExportGbxmlHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: written to the exact path asked for (output_path, or output_folder + file_name) through a
// staging folder; an existing file is replaced only with overwrite; output_folder is created when missing;
// errors are coded failures. On Revit 2026 and later the exporter needs a modifiable document, so it runs in
// a transaction that is always rolled back (ExportHelpers.RunRolledBack); earlier years open none.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Export
{
    public sealed class ExportGbxmlHandler : IRevitCommand
    {
        public string Name => "export_gbxml";
        public string Description =>
            "Export the model's energy analytical data to one gbXML (.xml) file at output_path, or output_folder + file_name "
            + "(absolute; .xml added; folder created if missing). Needs rooms or spaces and valid energy settings. "
            + "An existing file is replaced only with overwrite: true. The model is not changed.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + ExportHelpers.OutputSchema + @"}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var overwrite = p.Value<bool?>("overwrite") ?? false;
            if (!OutputFile.TryResolve(p.Value<string>("output_path"), p.Value<string>("output_folder"), p.Value<string>("file_name"), ".xml",
                    overwrite, out var target, out var failure)) return failure;

            var result = ExportHelpers.ExportSingle("gbxml", target, "*.xml", overwrite, staging =>
            {
                using (var options = new GBXMLExportOptions())
                {
#if REVIT2026 || REVIT2027
                    var ok = ExportHelpers.RunRolledBack(doc, "B2M: Export gbXML (rolled back)", () => doc.Export(staging, "export", options));
#else
                    var ok = doc.Export(staging, "export", options);
#endif
                    if (!ok) throw new InvalidOperationException("Revit reported that the gbXML export did not succeed.");
                }
            });
            if (!result.Success)
                return CommandResult.Fail(result.Error + " gbXML needs rooms or spaces with energy analytical data and valid energy settings (Manage > Energy Settings).");
            return result;
        }
    }
}
