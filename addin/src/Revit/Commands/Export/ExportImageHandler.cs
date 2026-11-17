// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Export/ExportImageHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: written to the exact path asked for (output_path, or output_folder + file_name, extension
// from image_format) through a staging folder instead of diffing the target folder for the file Revit named;
// an existing file is replaced only with overwrite; output_folder is created when missing; pixel_size is
// range-checked; a failed export is a coded failure instead of success:true with exported:false.
// No transaction is opened.

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Export
{
    public sealed class ExportImageHandler : IRevitCommand
    {
        private const int MinPixels = 32;
        private const int MaxPixels = 15000;

        public string Name => "export_image";
        public string Description =>
            "Export one view or sheet to a PNG or JPEG image at output_path, or output_folder + file_name (absolute; extension added; folder created if missing). "
            + "view_id default: the active view. pixel_size (default 2048, 32-15000) is the width in pixels. "
            + "An existing file is replaced only with overwrite: true.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + ExportHelpers.OutputSchema + @",
            ""view_id"":{""type"":""integer""},
            ""pixel_size"":{""type"":""integer""},
            ""image_format"":{""type"":""string"",""enum"":[""png"",""jpeg""]}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var overwrite = p.Value<bool?>("overwrite") ?? false;
            var jpeg = (p.Value<string>("image_format") ?? "png") == "jpeg";
            var pixels = p.Value<int?>("pixel_size") ?? 2048;
            if (pixels < MinPixels || pixels > MaxPixels)
                return CommandResult.Fail("INVALID_PARAMS: pixel_size must be between " + MinPixels + " and " + MaxPixels + ".");

            var extension = jpeg ? ".jpg" : ".png";
            var path = p.Value<string>("output_path");
            var name = p.Value<string>("file_name");
            // Accept ".jpeg" as given rather than appending ".jpg" to it.
            if (jpeg && ((path ?? name ?? "").EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))) extension = ".jpeg";
            if (!OutputFile.TryResolve(path, p.Value<string>("output_folder"), name, extension, overwrite, out var target, out var failure)) return failure;

            Autodesk.Revit.DB.View view;
            var usedActive = p["view_id"] == null;
            if (usedActive)
            {
                view = doc.ActiveView;
                if (view == null || view.IsTemplate) return CommandResult.Fail("NO_ACTIVE_VIEW: there is no exportable active view; pass view_id.");
            }
            else if (!Lookup.TryView(doc, p.Value<long>("view_id"), out view, out failure))
            {
                return failure;
            }

            var fileType = jpeg ? ImageFileType.JPEGLossless : ImageFileType.PNG;
            return ExportHelpers.ExportSingle(jpeg ? "jpeg" : "png", target, jpeg ? "*.jp*g" : "*.png", overwrite, staging =>
            {
                var options = new ImageExportOptions
                {
                    ExportRange = ExportRange.SetOfViews,
                    FilePath = System.IO.Path.Combine(staging, "export"),
                    HLRandWFViewsFileType = fileType,
                    ShadowViewsFileType = fileType,
                    ImageResolution = ImageResolution.DPI_300,
                    PixelSize = pixels,
                    FitDirection = FitDirectionType.Horizontal,
                    ZoomType = ZoomFitType.FitToPage
                };
                options.SetViewsAndSheets(new List<ElementId> { view.Id });
                doc.ExportImage(options);
            }, new { view_id = RevitCompat.GetId(view.Id), view_name = view.Name, pixel_size = pixels, used_active_view = usedActive });
        }
    }
}
