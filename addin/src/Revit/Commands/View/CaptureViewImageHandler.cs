// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Views/CaptureViewImageHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: PNG only, written to the exact absolute path asked for (output_path, or
// output_folder + file_name) through a staging folder instead of rvt-mcp's CaptureOutputPath allow-list
// and CaptureOutputResolver; an existing file is replaced only with overwrite; the returned size is the
// PNG's real pixel width and height read from the file.

using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using B2M.Revit.Commands.Export;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.View
{
    // B2M.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class CaptureViewImageHandler : IRevitCommand
    {
        public string Name => "capture_view_image";
        public string Description =>
            "Export a view or sheet (view_id, default the active view) to a PNG at output_path, or at output_folder + file_name "
            + "(absolute). pixel_size is the image width (default 1600). Returns the path and the image's pixel width and height.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""view_id"":{""type"":""integer""},
            ""output_path"":{""type"":""string""},
            ""output_folder"":{""type"":""string""},
            ""file_name"":{""type"":""string""},
            ""pixel_size"":{""type"":""integer""},
            ""overwrite"":{""type"":""boolean""}}}";
        // Writes a file, so it is not read-only even though the model is unchanged.
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var pixelSize = p.Value<int?>("pixel_size") ?? 1600;
            if (pixelSize < 32 || pixelSize > 15000) return CommandResult.Fail("INVALID_PARAMS: pixel_size must be 32 to 15000.");
            var overwrite = p.Value<bool?>("overwrite") ?? false;
            if (!OutputFile.TryResolve(p.Value<string>("output_path"), p.Value<string>("output_folder"), p.Value<string>("file_name"),
                    ".png", overwrite, out var target, out var failure)) return failure;

            View view;
            var viewId = p.Value<long?>("view_id");
            if (viewId.HasValue)
            {
                if (!Lookup.TryView(doc, viewId.Value, out view, out failure)) return failure;
            }
            else
            {
                view = uidoc.ActiveView;
                if (view == null) return CommandResult.Fail("NO_ACTIVE_VIEW: the document has no active view; pass view_id.");
            }

            string staging = null;
            try
            {
                staging = OutputFile.NewStagingFolder(target);
                var options = new ImageExportOptions
                {
                    FilePath = Path.Combine(staging, "capture"),
                    ZoomType = ZoomFitType.FitToPage,
                    PixelSize = pixelSize,
                    FitDirection = FitDirectionType.Horizontal,
                    ImageResolution = ImageResolution.DPI_150,
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    ExportRange = ExportRange.SetOfViews
                };
                options.SetViewsAndSheets(new List<ElementId> { view.Id });
                doc.ExportImage(options);
                OutputFile.Deliver(staging, "*.png", target, overwrite);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("EXPORT_FAILED: could not export view '" + view.Name + "' to PNG: " + ex.Message);
            }
            finally
            {
                OutputFile.Cleanup(staging);
            }

            var size = PngSize(target);
            return CommandResult.Ok(new
            {
                path = target,
                view_id = RevitCompat.GetId(view.Id),
                view_name = view.Name,
                width_px = size?.Item1,
                height_px = size?.Item2
            });
        }

        /// <summary>Width and height from the PNG IHDR chunk (big-endian at bytes 16 and 20); null when unreadable.</summary>
        private static Tuple<int, int> PngSize(string path)
        {
            try
            {
                var header = new byte[24];
                using (var stream = File.OpenRead(path))
                    if (stream.Read(header, 0, header.Length) != header.Length) return null;
                if (header[12] != 'I' || header[13] != 'H' || header[14] != 'D' || header[15] != 'R') return null;
                int BigEndian(int at) => (header[at] << 24) | (header[at + 1] << 16) | (header[at + 2] << 8) | header[at + 3];
                return Tuple.Create(BigEndian(16), BigEndian(20));
            }
            catch
            {
                return null;
            }
        }
    }
}
