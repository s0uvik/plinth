// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Export/BatchExportSheetsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: one file per sheet named file_name_prefix + "number - name", each exported through a
// staging folder and moved into place; every target is checked before anything is exported, so an existing
// file is replaced only with overwrite (rvt-mcp exported straight into the folder and reported files by
// timestamp); output_folder is created when missing; DWG sheets are merged into one file each (no viewport
// xrefs); a sheet that fails is listed and the rest still export (failedCount); sheets go in sheet-number
// order; non-printable sheets are refused up front. No transaction is opened.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Export
{
    using View = Autodesk.Revit.DB.View;

    public sealed class BatchExportSheetsHandler : IRevitCommand
    {
        public string Name => "batch_export_sheets";
        public string Description =>
            "Export sheets to PDF or DWG, one file per sheet named file_name_prefix + 'number - name' in output_folder (absolute; created if missing). "
            + "sheet_ids picks sheets; without it every printable sheet is exported, optionally narrowed by sheet_number_filter (substring). "
            + "Existing files are replaced only with overwrite: true. For one combined PDF use export_pdf.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""output_folder"":{""type"":""string""},
            ""format"":{""type"":""string"",""enum"":[""pdf"",""dwg""]},
            ""sheet_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""sheet_number_filter"":{""type"":""string""},
            ""file_name_prefix"":{""type"":""string""},
            ""overwrite"":{""type"":""boolean""}},
            ""required"":[""output_folder"",""format""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            var format = p.Value<string>("format");
            if (format == "dwg" && !OptionalFunctionalityUtils.IsDWGExportAvailable()) return ExportHelpers.ExporterMissing("DWG");

            var overwrite = p.Value<bool?>("overwrite") ?? false;
            if (!ExportHelpers.TryFolder(p.Value<string>("output_folder"), out var folder, out var failure)) return failure;

            var filter = p.Value<string>("sheet_number_filter");
            var sheets = new List<View>();
            if (p["sheet_ids"] is JArray ids)
            {
                if (!string.IsNullOrEmpty(filter)) return CommandResult.Fail("INVALID_PARAMS: sheet_number_filter applies only when sheet_ids is omitted.");
                if (ids.Count == 0) return CommandResult.Fail("INVALID_PARAMS: sheet_ids is empty; omit it to export every sheet.");
                foreach (var token in ids)
                {
                    if (!Lookup.TrySheet(doc, token.Value<long>(), null, out var sheet, out failure)) return failure;
                    if (sheets.Any(s => s.Id == sheet.Id)) return CommandResult.Fail("INVALID_PARAMS: sheet " + token + " is listed twice.");
                    if (!sheet.CanBePrinted) return CommandResult.Fail("NOT_PRINTABLE: sheet " + sheet.SheetNumber + " cannot be exported.");
                    sheets.Add(sheet);
                }
            }
            else
            {
                sheets.AddRange(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Where(s => !s.IsPlaceholder && !s.IsTemplate && s.CanBePrinted)
                    .Where(s => string.IsNullOrEmpty(filter) || (s.SheetNumber ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase));
                if (sheets.Count == 0)
                    return CommandResult.Fail(string.IsNullOrEmpty(filter)
                        ? "NO_SHEETS: the project has no printable sheets."
                        : "NO_SHEETS: no printable sheet number contains '" + filter + "'.");
            }
            if (sheets.Count > ExportHelpers.MaxViews)
                return CommandResult.Fail("INVALID_PARAMS: " + sheets.Count + " sheets is more than " + ExportHelpers.MaxViews + " per call; narrow with sheet_number_filter or sheet_ids.");

            var extension = "." + format;
            if (!ExportHelpers.TryPlanTargets(folder, sheets, null, p.Value<string>("file_name_prefix"), extension, overwrite, out var targets, out failure))
                return failure;

            return ExportHelpers.ExportEach(format, sheets, targets, "*" + extension, overwrite, (staging, sheet) =>
            {
                bool ok;
                if (format == "pdf")
                {
                    var options = new PDFExportOptions
                    {
                        Combine = true,
                        FileName = "export",
                        HideCropBoundaries = true,
                        HideScopeBoxes = true,
                        HideReferencePlane = true
                    };
                    ok = doc.Export(staging, new List<ElementId> { sheet.Id }, options);
                }
                else
                {
                    var options = new DWGExportOptions { MergedViews = true };
                    ok = doc.Export(staging, "export", new List<ElementId> { sheet.Id }, options);
                }
                if (!ok) throw new InvalidOperationException("Revit reported that the " + format.ToUpperInvariant() + " export did not succeed.");
            }, new { output_folder = folder, sheet_count = sheets.Count });
        }
    }
}
