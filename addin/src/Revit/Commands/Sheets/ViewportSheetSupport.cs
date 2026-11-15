// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Sheets/ViewportSheetSupport.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: feet/mm conversion goes through Mm; sheet resolution moved to Lookup.TrySheet (a miss
// now lists the valid sheets); the saved-position check uses B2M's REVIT2026/REVIT2027 constants; the
// snapshot and overflow DTOs used only by rvt-mcp's unported sheet-layout tools are dropped.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using B2M.Revit.Bridge;

namespace B2M.Revit.Commands.Sheets
{
    // B2M.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    /// <summary>One viewport's geometry on a sheet, in feet, with the reasons any rectangle is unavailable.</summary>
    internal sealed class ViewportSnapshot
    {
        public Viewport Viewport { get; set; }
        public long ViewportId { get; set; }
        public long? ViewId { get; set; }
        public string ViewName { get; set; }
        public string ViewType { get; set; }
        public double Scale { get; set; }
        public string Rotation { get; set; }
        public bool Pinned { get; set; }
        /// <summary>Name of the saved position the viewport is bound to; null when none (or before Revit 2026).</summary>
        public string SavedPosition { get; set; }
        public bool CropActive { get; set; }
        public bool CropVisible { get; set; }
        public double BoxCenterX { get; set; }
        public double BoxCenterY { get; set; }
        public SheetRect? Outline { get; set; }
        public SheetRect? Crop { get; set; }
        public SheetRect? Used { get; set; }
        public string RectSource { get; set; }
        public SheetMargins? Overflow { get; set; }
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>Shared sheet and viewport reading for the sheet tools.</summary>
    internal static class ViewportSheetSupport
    {
        // Annotation that hangs past the crop is flagged above this size (0.1 mm).
        private static readonly double OverflowWarnFeet = Mm.ToFeet(0.1);
        private const double DegenerateFeet = 1e-9;

        public const string CoordinateFrame =
            "Sheet paper space in millimetres: +X right, +Y up, origin at the sheet's own origin. "
            + "These are not project coordinates; do not mix them with model coordinates or feet.";

        public const string AlignmentRectNote =
            "visible_rect_sheet_mm is the rectangle used for alignment: the crop region when the view crop is active, "
            + "otherwise the view outline. view_outline_sheet_mm also covers annotation that extends past the crop.";

        /// <summary>Sheet-space feet to millimetres at 0.001 mm.</summary>
        public static double ToMm(double feet) => Mm.FromFeet(feet, 3);

        public static object RectDto(SheetRect? rect)
        {
            if (!rect.HasValue) return null;
            var r = rect.Value;
            return new
            {
                min_x = ToMm(r.MinX),
                min_y = ToMm(r.MinY),
                max_x = ToMm(r.MaxX),
                max_y = ToMm(r.MaxY),
                width = ToMm(r.Width),
                height = ToMm(r.Height),
                center_x = ToMm(r.CenterX),
                center_y = ToMm(r.CenterY)
            };
        }

        public static List<Viewport> GetViewports(Document doc, ViewSheet sheet)
        {
            return sheet.GetAllViewports()
                .Select(id => doc.GetElement(id) as Viewport)
                .Where(vp => vp != null)
                .OrderBy(vp => RevitCompat.GetId(vp.Id))
                .ToList();
        }

        /// <summary>Union of the title block bounding boxes on the sheet; null when there is none.</summary>
        public static SheetRect? GetSheetFrame(Document doc, ViewSheet sheet)
        {
            var rects = new List<SheetRect>();
            foreach (var element in new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType())
            {
                var box = element.get_BoundingBox(sheet);
                if (box != null)
                    rects.Add(new SheetRect(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y));
            }
            return rects.Count == 0 ? (SheetRect?)null : ViewportLayoutMath.Union(rects);
        }

        /// <summary>
        /// The saved position a viewport is bound to. Revit re-applies it at regeneration, so a move of
        /// such a viewport is silently undone. Saved positions exist from Revit 2026; earlier versions
        /// have no such parameter and report none.
        /// </summary>
        public static string ReadSavedPosition(Document doc, Viewport vp, List<string> warnings)
        {
#if REVIT2026 || REVIT2027
            try
            {
                var id = vp.get_Parameter(BuiltInParameter.VIEW_POSITION)?.AsElementId();
                if (id == null || id == ElementId.InvalidElementId) return null;
                return doc.GetElement(id)?.Name ?? "(unnamed)";
            }
            catch (Exception)
            {
                warnings.Add("saved_position_unreadable");
                return null;
            }
#else
            return null;
#endif
        }

        public static ViewportSnapshot Read(Document doc, Viewport vp)
        {
            var snap = new ViewportSnapshot
            {
                Viewport = vp,
                ViewportId = RevitCompat.GetId(vp.Id),
                Pinned = vp.Pinned,
                Rotation = vp.Rotation.ToString()
            };
            snap.SavedPosition = ReadSavedPosition(doc, vp, snap.Warnings);

            var view = doc.GetElement(vp.ViewId) as View;
            if (view == null)
            {
                snap.Warnings.Add("view_not_found");
                return snap;
            }

            snap.ViewId = RevitCompat.GetId(view.Id);
            snap.ViewName = view.Name;
            snap.ViewType = view.ViewType.ToString();
            snap.Scale = view.Scale;

            try
            {
                snap.CropActive = view.CropBoxActive;
                snap.CropVisible = view.CropBoxVisible;
            }
            catch (Exception)
            {
                snap.Warnings.Add("crop_flags_unavailable");
            }

            var center = vp.GetBoxCenter();
            if (center == null)
            {
                snap.Warnings.Add("viewport_center_unavailable");
                return snap;
            }
            snap.BoxCenterX = center.X;
            snap.BoxCenterY = center.Y;

            if (vp.Rotation != ViewportRotation.None)
            {
                snap.Warnings.Add("rotated_viewport_not_supported");
                return snap;
            }
            if (view is View3D view3D && view3D.IsPerspective)
            {
                snap.Warnings.Add("perspective_view_not_supported");
                return snap;
            }
            if (snap.Scale <= 0)
            {
                snap.Warnings.Add("view_scale_invalid");
                return snap;
            }

            BoundingBoxUV outline = null;
            try { outline = view.Outline; }
            catch (Exception) { }
            if (outline == null)
            {
                snap.Warnings.Add("view_outline_unavailable");
                return snap;
            }

            snap.Outline = ViewportLayoutMath.OutlineOnSheet(
                center.X, center.Y, outline.Min.U, outline.Min.V, outline.Max.U, outline.Max.V);

            if (snap.CropActive)
            {
                BoundingBoxXYZ crop = null;
                try { crop = view.CropBox; }
                catch (Exception) { }

                if (crop != null)
                {
                    var rect = ViewportLayoutMath.CropRegionOnSheet(
                        center.X, center.Y, outline.Min.U, outline.Min.V, outline.Max.U, outline.Max.V,
                        crop.Min.X, crop.Min.Y, crop.Max.X, crop.Max.Y, snap.Scale);
                    if (rect.Width > DegenerateFeet && rect.Height > DegenerateFeet)
                        snap.Crop = rect;
                    else
                        snap.Warnings.Add("crop_region_degenerate");
                }
            }

            if (snap.Crop.HasValue)
            {
                snap.Used = snap.Crop;
                snap.RectSource = "crop_region";
                snap.Overflow = ViewportLayoutMath.OverflowBeyond(snap.Outline.Value, snap.Crop.Value);
                if (snap.Overflow.Value.Max > OverflowWarnFeet)
                    snap.Warnings.Add("annotation_extends_past_crop");
            }
            else
            {
                snap.Used = snap.Outline;
                snap.RectSource = "view_outline";
                if (!snap.CropActive)
                    snap.Warnings.Add("crop_inactive_view_outline_used");
            }

            return snap;
        }
    }
}
