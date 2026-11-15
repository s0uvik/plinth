// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Export/ExportPdfHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M (PRD: drawing set PDF): always one combined PDF at output_folder\file_name.pdf, written
// through a staging folder inside output_folder and then moved into place, so nothing outside that folder
// is touched and an existing file is replaced only with overwrite (rvt-mcp exported straight into the
// folder and reported files by timestamp); output_folder is created when missing; without view_ids every
// sheet is exported in sheet-number order (rvt-mcp exported the active view); unprintable and duplicate
// views are refused before export; crop boundaries, scope boxes and reference planes are hidden.

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
    // B2M.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class ExportPdfHandler : IRevitCommand
    {
        public string Name => "export_pdf";
        public string Description =>
            "Export sheets or views into ONE combined PDF at output_folder\\file_name(.pdf). output_folder must be absolute (created if missing). "
            + "view_ids sets the pages in order; default is every sheet by sheet number. An existing file is replaced only with overwrite: true. "
            + "Returns the full path written.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""output_folder"":{""type"":""string""},
            ""file_name"":{""type"":""string""},
            ""view_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""overwrite"":{""type"":""boolean""}},
            ""required"":[""output_folder"",""file_name""]}";
        // Writes a file, so it is not read-only even though the model is unchanged.
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var overwrite = p.Value<bool?>("overwrite") ?? false;
            if (!OutputFile.TryResolve(null, p.Value<string>("output_folder"), p.Value<string>("file_name"), ".pdf", overwrite,
                    out var target, out var failure)) return failure;

            var views = new List<View>();
            if (p["view_ids"] is JArray ids && ids.Count > 0)
            {
                foreach (var token in ids)
                {
                    if (!Lookup.TryView(doc, token.Value<long>(), out var view, out failure)) return failure;
                    if (views.Any(v => v.Id == view.Id)) return CommandResult.Fail("INVALID_PARAMS: view " + token + " is listed twice.");
                    if (!view.CanBePrinted) return CommandResult.Fail("NOT_PRINTABLE: view '" + view.Name + "' (" + view.ViewType + ") cannot be exported to PDF.");
                    if (view is ViewSheet sheet && sheet.IsPlaceholder)
                        return CommandResult.Fail("SHEET_IS_PLACEHOLDER: sheet " + sheet.SheetNumber + " is a placeholder and cannot be exported.");
                    views.Add(view);
                }
            }
            else
            {
                views.AddRange(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Where(s => !s.IsPlaceholder && s.CanBePrinted).OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase));
                if (views.Count == 0) return CommandResult.Fail("NO_SHEETS: the project has no printable sheets; pass view_ids.");
            }

            string staging = null;
            try
            {
                staging = OutputFile.NewStagingFolder(target);
                var options = new PDFExportOptions
                {
                    Combine = true,
                    FileName = "export",
                    HideCropBoundaries = true,
                    HideScopeBoxes = true,
                    HideReferencePlane = true
                };
                if (!doc.Export(staging, views.Select(v => v.Id).ToList(), options))
                    return CommandResult.Fail("EXPORT_FAILED: Revit reported that at least one view did not export. Nothing was written to the output folder.");
                OutputFile.Deliver(staging, "*.pdf", target, overwrite);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("EXPORT_FAILED: PDF export failed: " + ex.Message);
            }
            finally
            {
                OutputFile.Cleanup(staging);
            }

            return CommandResult.Ok(new
            {
                path = target,
                size_bytes = new FileInfo(target).Length,
                view_count = views.Count,
                views = views.Select(v => new
                {
                    id = RevitCompat.GetId(v.Id),
                    name = v.Name,
                    sheet_number = (v as ViewSheet)?.SheetNumber
                })
            });
        }
    }
}
