// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Views/SetViewCropHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: bounds are model coordinates in mm projected into the crop box's own frame (rvt-mcp
// wrote them as crop-local values with no transform, which misplaced the crop on sections, elevations and
// rotated plans, and defaulted any missing coordinate to 0); bounds needs all four x/y values, z is optional;
// the view depth (crop-local Z, the far clip) is never changed; bounds and fit_element_ids together are an
// error; unknown fit ids, or ids with no extent in the view, are errors (rvt-mcp skipped them and could
// silently leave the crop alone); a call that changes nothing is an error; the result reports the crop extent.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.View
{
    // Plinth.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class SetViewCropHandler : IRevitCommand
    {
        private static readonly string[] BoundsKeys = { "min_x_mm", "min_y_mm", "min_z_mm", "max_x_mm", "max_y_mm", "max_z_mm" };

        public string Name => "set_view_crop";
        public string Description =>
            "Change a view's crop (view_id, default the active view): enabled turns cropping on/off, visible shows/hides the crop "
            + "region; bounds {min_x_mm, min_y_mm, max_x_mm, max_y_mm, optional min_z_mm/max_z_mm} is a model-space box in mm, "
            + "or fit_element_ids crops to those elements plus padding_mm (default 100). The crop region becomes the box's extent "
            + "seen in the view; the view depth is not changed.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""view_id"":{""type"":""integer""},
            ""enabled"":{""type"":""boolean""},
            ""visible"":{""type"":""boolean""},
            ""bounds"":{""type"":""object""},
            ""fit_element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""padding_mm"":{""type"":""number""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            if (!ViewHelpers.TryResolveView(uidoc, p.Value<long?>("view_id"), null, false, out var view, out var failure)) return failure;

            var enabled = p.Value<bool?>("enabled");
            var visible = p.Value<bool?>("visible");
            var bounds = p["bounds"] as JObject;
            var fitIds = p["fit_element_ids"] as JArray;
            var padding = p.Value<double?>("padding_mm") ?? 100;
            if (bounds != null && fitIds != null)
                return CommandResult.Fail("INVALID_PARAMS: pass bounds or fit_element_ids, not both.");
            if (padding < 0) return CommandResult.Fail("INVALID_PARAMS: padding_mm cannot be negative.");
            if (p["padding_mm"] != null && fitIds == null)
                return CommandResult.Fail("INVALID_PARAMS: padding_mm only applies with fit_element_ids.");
            if (!enabled.HasValue && !visible.HasValue && bounds == null && fitIds == null)
                return CommandResult.Fail("INVALID_PARAMS: pass at least one of enabled, visible, bounds, fit_element_ids.");
            if (view is View3D v3 && v3.IsPerspective && (bounds != null || fitIds != null))
                return CommandResult.Fail("PERSPECTIVE_VIEW: the crop region of perspective view '" + view.Name + "' cannot be set by bounds.");

            BoundingBoxXYZ current;
            try { current = view.CropBox; }
            catch (Exception ex) { return CommandResult.Fail("NO_CROP: view '" + view.Name + "' (" + view.ViewType + ") has no crop box: " + ex.Message); }
            if (current == null) return CommandResult.Fail("NO_CROP: view '" + view.Name + "' (" + view.ViewType + ") has no crop box.");

            XYZ min = null, max = null;
            if (bounds != null && !TryBounds(bounds, current, out min, out max, out failure)) return failure;
            if (fitIds != null && !TryFit(doc, view, fitIds, Mm.ToFeet(padding), out min, out max, out failure)) return failure;
            var newBox = min == null ? null : ViewHelpers.CropToModelBox(current, min, max);

            using (var tx = new Transaction(doc, "Plinth: Set View Crop"))
            {
                tx.Start();
                try
                {
                    if (enabled.HasValue) view.CropBoxActive = enabled.Value;
                    if (visible.HasValue) view.CropBoxVisible = visible.Value;
                    if (newBox != null) view.CropBox = newBox;
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    var hint = view.ViewTemplateId != ElementId.InvalidElementId ? " The view's template may control the crop." : "";
                    return CommandResult.Fail("CROP_FAILED: could not change the crop of '" + view.Name + "': " + ex.Message + hint + " Nothing was changed.");
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["view_name"] = view.Name,
                ["crop_active"] = view.CropBoxActive,
                ["crop_visible"] = view.CropBoxVisible,
                ["bounds_updated"] = newBox != null,
                ["crop_extent_model_mm"] = ViewHelpers.CropExtentMm(view.CropBox)
            });
        }

        private static bool TryBounds(JObject bounds, BoundingBoxXYZ current, out XYZ min, out XYZ max, out CommandResult failure)
        {
            min = max = null;
            failure = null;
            var stray = bounds.Properties().Select(x => x.Name).Where(n => !BoundsKeys.Contains(n)).ToList();
            if (stray.Count > 0)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: unknown bounds field(s) " + string.Join(", ", stray) + ". Valid: " + string.Join(", ", BoundsKeys) + ".");
                return false;
            }
            var values = new Dictionary<string, double>();
            foreach (var key in BoundsKeys)
            {
                var t = bounds[key];
                if (t == null || t.Type == JTokenType.Null) continue;
                if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float)
                {
                    failure = CommandResult.Fail("INVALID_PARAMS: bounds." + key + " must be a number (mm).");
                    return false;
                }
                values[key] = t.Value<double>();
            }
            var missing = new[] { "min_x_mm", "min_y_mm", "max_x_mm", "max_y_mm" }.Where(k => !values.ContainsKey(k)).ToList();
            if (missing.Count > 0)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: bounds needs " + string.Join(", ", missing) + ".");
                return false;
            }
            if (values.ContainsKey("min_z_mm") != values.ContainsKey("max_z_mm"))
            {
                failure = CommandResult.Fail("INVALID_PARAMS: give both min_z_mm and max_z_mm, or neither.");
                return false;
            }
            if (values["min_x_mm"] >= values["max_x_mm"] || values["min_y_mm"] >= values["max_y_mm"]
                || (values.ContainsKey("min_z_mm") && values["min_z_mm"] > values["max_z_mm"]))
            {
                failure = CommandResult.Fail("INVALID_PARAMS: each bounds min must be smaller than its max.");
                return false;
            }

            double zMin, zMax;
            if (values.ContainsKey("min_z_mm"))
            {
                zMin = Mm.ToFeet(values["min_z_mm"]);
                zMax = Mm.ToFeet(values["max_z_mm"]);
            }
            else
            {
                // No z given: use the current crop's model height range, so plans keep their depth and
                // sections keep their vertical extent.
                var a = current.Transform.OfPoint(current.Min);
                var b = current.Transform.OfPoint(current.Max);
                zMin = Math.Min(a.Z, b.Z);
                zMax = Math.Max(a.Z, b.Z);
            }
            min = new XYZ(Mm.ToFeet(values["min_x_mm"]), Mm.ToFeet(values["min_y_mm"]), zMin);
            max = new XYZ(Mm.ToFeet(values["max_x_mm"]), Mm.ToFeet(values["max_y_mm"]), zMax);
            return true;
        }

        private static bool TryFit(Document doc, View view, JArray ids, double padding, out XYZ min, out XYZ max, out CommandResult failure)
        {
            min = max = null;
            failure = null;
            if (ids.Count == 0)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: fit_element_ids cannot be empty.");
                return false;
            }
            var missing = new List<long>();
            var noExtent = new List<long>();
            foreach (var token in ids)
            {
                var id = token.Value<long>();
                var element = doc.GetElement(RevitCompat.ToElementId(id));
                if (element == null) { missing.Add(id); continue; }
                var box = element.get_BoundingBox(view);
                if (box == null) { noExtent.Add(id); continue; }
                var a = box.Transform.OfPoint(box.Min);
                var b = box.Transform.OfPoint(box.Max);
                var lo = new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
                var hi = new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
                min = min == null ? lo : new XYZ(Math.Min(min.X, lo.X), Math.Min(min.Y, lo.Y), Math.Min(min.Z, lo.Z));
                max = max == null ? hi : new XYZ(Math.Max(max.X, hi.X), Math.Max(max.Y, hi.Y), Math.Max(max.Z, hi.Z));
            }
            if (missing.Count > 0 || noExtent.Count > 0)
            {
                failure = CommandResult.Fail(missing.Count > 0
                        ? "ELEMENT_NOT_FOUND: " + missing.Count + " fit_element_ids are not elements in this document."
                        : "NO_EXTENT: " + noExtent.Count + " fit_element_ids have no extent in view '" + view.Name + "'.",
                    new JObject { ["missing_ids"] = new JArray(missing), ["no_extent_in_view_ids"] = new JArray(noExtent) });
                min = max = null;
                return false;
            }
            min = new XYZ(min.X - padding, min.Y - padding, min.Z - padding);
            max = new XYZ(max.X + padding, max.Y + padding, max.Z + padding);
            return true;
        }
    }
}
