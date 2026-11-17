// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Export/ExportFbxHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: written to the exact path asked for (output_path, or output_folder + file_name) through a
// staging folder; an existing file is replaced only with overwrite; output_folder is created when missing; an
// unavailable FBX exporter fails with EXPORTER_NOT_AVAILABLE; a non-3D view fails with the 3D views to pick
// from. No transaction is opened.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Export
{
    public sealed class ExportFbxHandler : IRevitCommand
    {
        public string Name => "export_fbx";
        public string Description =>
            "Export one 3D view to an Autodesk FBX file at output_path, or output_folder + file_name (absolute; .fbx added; folder created if missing). "
            + "view_id: a 3D view; default the active view, which must then be 3D. An existing file is replaced only with overwrite: true.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + ExportHelpers.OutputSchema + @",
            ""view_id"":{""type"":""integer""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!OptionalFunctionalityUtils.IsFBXExportAvailable()) return ExportHelpers.ExporterMissing("FBX");

            var overwrite = p.Value<bool?>("overwrite") ?? false;
            if (!OutputFile.TryResolve(p.Value<string>("output_path"), p.Value<string>("output_folder"), p.Value<string>("file_name"), ".fbx",
                    overwrite, out var target, out var failure)) return failure;

            Autodesk.Revit.DB.View view;
            var usedActive = p["view_id"] == null;
            if (usedActive)
                view = doc.ActiveView;
            else if (!Lookup.TryView(doc, p.Value<long>("view_id"), out view, out failure))
                return failure;
            if (!(view is View3D view3d) || view3d.IsTemplate)
            {
                var valid = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate)
                    .OrderBy(v => v.Name).Take(100).Select(v => new JObject { ["id"] = RevitCompat.GetId(v.Id), ["name"] = v.Name });
                return CommandResult.Fail("NOT_A_3D_VIEW: " + (view == null ? "there is no active view" : "view '" + view.Name + "' is not a 3D view")
                    + "; pass view_id of a 3D view.", new JObject { ["valid_views"] = new JArray(valid) });
            }

            return ExportHelpers.ExportSingle("fbx", target, "*.fbx", overwrite, staging =>
            {
                var set = new ViewSet();
                set.Insert(view3d);
                if (!doc.Export(staging, "export", set, new FBXExportOptions()))
                    throw new InvalidOperationException("Revit reported that the FBX export did not succeed.");
            }, new { view_id = RevitCompat.GetId(view3d.Id), view_name = view3d.Name, used_active_view = usedActive });
        }
    }
}
