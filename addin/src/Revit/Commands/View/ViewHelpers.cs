// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// files src/shared/Handlers/Views/ViewPresentationSupport.cs and src/shared/Handlers/Views/ViewOrientationMath.cs,
// commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: view lookup returns "CODE: message" failures with the valid views (a name that matches
// several views is an error, rvt-mcp took the first); mm conversion goes through Mm; the camera-restore
// debug log is dropped; the model-to-crop-frame projection used by set_view_crop is new.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.View
{
    // B2M.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    /// <summary>View lookup and camera arithmetic shared by the view toolset.</summary>
    internal static class ViewHelpers
    {
        private const int MaxOptions = 100;

        /// <summary>
        /// A view by view_id, else by view_name (exact case first, then any case), else the active view.
        /// Templates are refused. A name shared by several views is an error listing them.
        /// </summary>
        public static bool TryResolveView(UIDocument uidoc, long? id, string name, bool threeDOnly, out View view, out CommandResult failure)
        {
            var doc = uidoc.Document;
            view = null;
            failure = null;
            if (id.HasValue && !string.IsNullOrEmpty(name))
            {
                failure = CommandResult.Fail("INVALID_PARAMS: pass view_id or view_name, not both.");
                return false;
            }
            if (id.HasValue)
            {
                if (!Lookup.TryView(doc, id.Value, out view, out failure)) return false;
            }
            else if (!string.IsNullOrWhiteSpace(name))
            {
                var candidates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate && (!threeDOnly || v is View3D) && IsUserView(v))
                    .OrderBy(v => v.Name).ToList();
                var matches = candidates.Where(v => string.Equals(v.Name, name, StringComparison.Ordinal)).ToList();
                if (matches.Count == 0)
                    matches = candidates.Where(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 1)
                {
                    view = matches[0];
                }
                else
                {
                    failure = CommandResult.Fail(matches.Count == 0
                            ? "VIEW_NOT_FOUND: no " + (threeDOnly ? "3D view" : "view") + " named '" + name + "'."
                            : "VIEW_AMBIGUOUS: " + matches.Count + " views are named '" + name + "'; pass view_id.",
                        new JObject { ["valid_views"] = Describe(matches.Count > 1 ? matches : candidates) });
                    return false;
                }
            }
            else
            {
                view = uidoc.ActiveView;
                if (view == null)
                {
                    failure = CommandResult.Fail("NO_ACTIVE_VIEW: the document has no active view; pass view_id or view_name.");
                    return false;
                }
            }

            if (threeDOnly && !(view is View3D))
            {
                failure = CommandResult.Fail("NOT_A_3D_VIEW: view '" + view.Name + "' is a " + view.ViewType + ", not a 3D view.");
                view = null;
                return false;
            }
            if (view.IsTemplate)
            {
                failure = CommandResult.Fail("VIEW_IS_TEMPLATE: view '" + view.Name + "' is a view template.");
                view = null;
                return false;
            }
            return true;
        }

        public static bool IsUserView(View v) =>
            v.ViewType != ViewType.Undefined && v.ViewType != ViewType.Internal
            && v.ViewType != ViewType.ProjectBrowser && v.ViewType != ViewType.SystemBrowser;

        public static JArray Describe(IEnumerable<View> views) =>
            new JArray(views.Take(MaxOptions).Select(v => new JObject
            {
                ["id"] = RevitCompat.GetId(v.Id),
                ["name"] = v.Name,
                ["viewType"] = v.ViewType.ToString()
            }));

        /// <summary>A [x,y,z] point in mm, converted to feet. Null with no failure when absent and optional.</summary>
        public static bool TryReadPointMm(JObject p, string key, bool required, out XYZ point, out CommandResult failure)
        {
            point = null;
            if (!TryReadVector(p, key, required, " in mm", out var v, out failure)) return false;
            if (v != null) point = new XYZ(Mm.ToFeet(v[0]), Mm.ToFeet(v[1]), Mm.ToFeet(v[2]));
            return true;
        }

        public static bool TryReadVector(JObject p, string key, bool required, string unit, out double[] values, out CommandResult failure)
        {
            values = null;
            failure = null;
            var token = p[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                if (!required) return true;
                failure = CommandResult.Fail("INVALID_PARAMS: " + key + " is required as [x,y,z]" + unit + ".");
                return false;
            }
            var array = token as JArray;
            if (array == null || array.Count != 3 || !array.All(t => t.Type == JTokenType.Integer || t.Type == JTokenType.Float))
            {
                failure = CommandResult.Fail("INVALID_PARAMS: " + key + " must be [x,y,z], three numbers" + unit + ".");
                return false;
            }
            values = array.Select(t => t.Value<double>()).ToArray();
            if (values.Any(x => double.IsNaN(x) || double.IsInfinity(x)))
            {
                failure = CommandResult.Fail("INVALID_PARAMS: " + key + " must be three finite numbers" + unit + ".");
                values = null;
                return false;
            }
            return true;
        }

        /// <summary>Eye, forward and an up vector made perpendicular to forward (falls back to +Z, +Y, +X).</summary>
        public static bool TryMakeOrientation(XYZ eye, XYZ target, double[] up, out ViewOrientation3D orientation, out CommandResult failure)
        {
            orientation = null;
            failure = null;
            var forward = target - eye;
            var trueUp = PerpendicularUp(new[] { forward.X, forward.Y, forward.Z }, up);
            if (trueUp == null)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: eye and target must be different points.");
                return false;
            }
            orientation = new ViewOrientation3D(eye, new XYZ(trueUp[0], trueUp[1], trueUp[2]), forward.Normalize());
            return true;
        }

        public static double[] PerpendicularUp(double[] forward, double[] up)
        {
            var f = Normalize(forward);
            if (f == null) return null;
            foreach (var candidate in new[] { up, new double[] { 0, 0, 1 }, new double[] { 0, 1, 0 }, new double[] { 1, 0, 0 } })
            {
                if (candidate == null || Length(candidate) < 1e-9) continue;
                var along = Dot(candidate, f);
                var trueUp = Normalize(new[] { candidate[0] - f[0] * along, candidate[1] - f[1] * along, candidate[2] - f[2] * along });
                if (trueUp != null) return trueUp;
            }
            return null;
        }

        /// <summary>SetOrientation moves Revit's temporary camera, which a rollback does not undo.</summary>
        public static void RestoreCamera(View3D view, ViewOrientation3D before)
        {
            // A restore failure must not replace the error that caused the rollback.
            try { view.SetOrientation(before); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        }

        public static JObject DescribeCamera(View3D view)
        {
            var o = view.GetOrientation();
            return new JObject
            {
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["view_name"] = view.Name,
                ["perspective"] = view.IsPerspective,
                ["eye"] = new JArray(Mm.FromFeet(o.EyePosition.X), Mm.FromFeet(o.EyePosition.Y), Mm.FromFeet(o.EyePosition.Z)),
                ["forward"] = Direction(o.ForwardDirection),
                ["up"] = Direction(o.UpDirection)
            };
        }

        /// <summary>ZoomToFit on the view's open UI window. Runs after the commit; false when the view is not open.</summary>
        public static bool TryZoomToFit(UIDocument uidoc, View view)
        {
            try
            {
                var uiView = uidoc.GetOpenUIViews().FirstOrDefault(u => u.ViewId == view.Id);
                if (uiView == null) return false;
                uiView.ZoomToFit();
                return true;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Projects a model-space box (feet) into a crop box's own frame and returns a copy of the crop box
        /// whose local X/Y span that projection. Local Z (view depth / far clip) is left as it was.
        /// </summary>
        public static BoundingBoxXYZ CropToModelBox(BoundingBoxXYZ crop, XYZ modelMin, XYZ modelMax)
        {
            var toLocal = crop.Transform.Inverse;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var x in new[] { modelMin.X, modelMax.X })
            foreach (var y in new[] { modelMin.Y, modelMax.Y })
            foreach (var z in new[] { modelMin.Z, modelMax.Z })
            {
                var local = toLocal.OfPoint(new XYZ(x, y, z));
                minX = Math.Min(minX, local.X); maxX = Math.Max(maxX, local.X);
                minY = Math.Min(minY, local.Y); maxY = Math.Max(maxY, local.Y);
            }
            return new BoundingBoxXYZ
            {
                Transform = crop.Transform,
                Min = new XYZ(minX, minY, crop.Min.Z),
                Max = new XYZ(maxX, maxY, crop.Max.Z)
            };
        }

        /// <summary>The crop box's model-space extent in mm.</summary>
        public static JObject CropExtentMm(BoundingBoxXYZ crop)
        {
            var points = new List<XYZ>();
            foreach (var x in new[] { crop.Min.X, crop.Max.X })
            foreach (var y in new[] { crop.Min.Y, crop.Max.Y })
            foreach (var z in new[] { crop.Min.Z, crop.Max.Z })
                points.Add(crop.Transform.OfPoint(new XYZ(x, y, z)));
            return new JObject
            {
                ["min_x_mm"] = Mm.FromFeet(points.Min(q => q.X)), ["min_y_mm"] = Mm.FromFeet(points.Min(q => q.Y)),
                ["min_z_mm"] = Mm.FromFeet(points.Min(q => q.Z)), ["max_x_mm"] = Mm.FromFeet(points.Max(q => q.X)),
                ["max_y_mm"] = Mm.FromFeet(points.Max(q => q.Y)), ["max_z_mm"] = Mm.FromFeet(points.Max(q => q.Z))
            };
        }

        private static JArray Direction(XYZ d) => new JArray(Math.Round(d.X, 4), Math.Round(d.Y, 4), Math.Round(d.Z, 4));

        private static double[] Normalize(double[] v)
        {
            var length = Length(v);
            if (length < 1e-6) return null;
            return new[] { v[0] / length, v[1] / length, v[2] / length };
        }

        private static double Length(double[] v) => Math.Sqrt(Dot(v, v));

        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    }
}
