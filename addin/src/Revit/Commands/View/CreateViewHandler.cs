// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Views/CreateViewHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M (PRD: drawing set views): adds ceiling plans, building sections (a plan line in mm,
// far-clip depth and bottom/top heights build the section box) and elevations (an ElevationMarker at a
// point on the level's floor plan, looking north/south/east/west); optional name, view type name,
// scale, detail level and a plan crop rectangle in mm; an unknown level is an error with the valid
// options (rvt-mcp fell back to the lowest level); a duplicate view name fails before anything is created;
// fields that do not apply to the chosen view type are rejected.
// Step 9 (drawing to model): a section can look to either side of its line (side left|right), take its bottom/top
// from levels plus offsets (bottomLevel/bottomOffset, topLevel/topOffset), and any view can get a view template
// (viewTemplateName, checked with IsValidViewTemplate). After creation the section's cut plane is read back and the
// view is rolled back if it does not pass through the given line within 1 mm (sectionLineMm reports where it is).
// Step 9 T10b: drafting views (ViewDrafting.Create with a Drafting view family type; scale, detail level, template)
// and structural plans (ViewPlan.Create with a StructuralPlan view family type on a level). Floor and structural plans
// take an optional viewRange {top_offset_mm, cut_offset_mm, bottom_offset_mm, depth_offset_mm, top_level?,
// bottom_level?, depth_level?}: offsets from the view's level, or from the named level for top/bottom/depth (the cut
// plane is always on the view's level, as in Revit). Planes not given keep the view type's defaults. The given planes
// are checked before anything is created, and the merged range again on the new view (top >= cut >= bottom >= depth);
// PlanViewRange is set on the new view only, never on a type or template.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using B2M.Revit.Core.Drawing;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.View
{
    // B2M.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class CreateViewHandler : IRevitCommand
    {
        private static readonly string[] Common = { "viewType", "name", "viewFamilyTypeName", "scale", "detailLevel", "viewTemplateName" };
        private static readonly string[] PlanFields = { "level", "cropMinX", "cropMinY", "cropMaxX", "cropMaxY" };
        private static readonly string[] RangedPlanFields = PlanFields.Concat(new[] { "viewRange" }).ToArray();
        private static readonly string[] ViewRangeFields =
            { "top_offset_mm", "cut_offset_mm", "bottom_offset_mm", "depth_offset_mm", "top_level", "bottom_level", "depth_level" };
        private static readonly string[] SectionFields =
            { "startX", "startY", "endX", "endY", "depth", "bottom", "top", "side", "bottomLevel", "bottomOffset", "topLevel", "topOffset" };
        private static readonly string[] SectionRequired = { "startX", "startY", "endX", "endY", "depth" };
        private static readonly string[] ElevationFields = { "level", "x", "y", "direction" };

        public string Name => "create_view";
        public string Description =>
            "Create a view. floor_plan / structural_plan / ceiling_plan: on a named level, optional crop rectangle (cropMinX..cropMaxY, mm); "
            + "floor and structural plans take an optional viewRange {top_offset_mm, cut_offset_mm, bottom_offset_mm, depth_offset_mm} "
            + "from the view's level (top_level / bottom_level / depth_level name another level for that plane; planes left out keep "
            + "the defaults; top >= cut >= bottom >= depth). drafting: an empty drafting view (scale, e.g. 500). 3d: isometric. section: cut exactly on the plan line startX/startY to endX/endY (mm), looking to the side (left, default, "
            + "or right) of start->end (drawn west to east, left looks north) for depth mm (far clip); its height range is bottom/top "
            + "(absolute mm) or bottomLevel/topLevel plus bottomOffset/topOffset (mm). "
            + "elevation: marker at x/y (mm) on the named level's floor plan; direction is the way the view looks (north = +Y). "
            + "Optional name (e.g. SECTION - AA), viewFamilyTypeName, viewTemplateName, scale (100 for 1:100), detailLevel.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""viewType"":{""type"":""string"",""enum"":[""floor_plan"",""ceiling_plan"",""3d"",""section"",""elevation"",""drafting"",""structural_plan""]},
            ""viewRange"":{""type"":""object""},
            ""level"":{""type"":""string""},
            ""name"":{""type"":""string""},
            ""viewFamilyTypeName"":{""type"":""string""},
            ""scale"":{""type"":""integer""},
            ""detailLevel"":{""type"":""string"",""enum"":[""coarse"",""medium"",""fine""]},
            ""cropMinX"":{""type"":""number""},""cropMinY"":{""type"":""number""},
            ""cropMaxX"":{""type"":""number""},""cropMaxY"":{""type"":""number""},
            ""startX"":{""type"":""number""},""startY"":{""type"":""number""},
            ""endX"":{""type"":""number""},""endY"":{""type"":""number""},
            ""depth"":{""type"":""number""},""bottom"":{""type"":""number""},""top"":{""type"":""number""},
            ""side"":{""type"":""string"",""enum"":[""left"",""right""]},
            ""bottomLevel"":{""type"":""string""},""bottomOffset"":{""type"":""number""},
            ""topLevel"":{""type"":""string""},""topOffset"":{""type"":""number""},
            ""viewTemplateName"":{""type"":""string""},
            ""x"":{""type"":""number""},""y"":{""type"":""number""},
            ""direction"":{""type"":""string"",""enum"":[""north"",""south"",""east"",""west""]}},
            ""required"":[""viewType""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var kind = p.Value<string>("viewType");
            string[] fields, required;
            ViewFamily family;
            ElementTypeGroup group;
            ViewType resultType;
            switch (kind)
            {
                case "floor_plan":
                    fields = RangedPlanFields; required = new[] { "level" };
                    family = ViewFamily.FloorPlan; group = ElementTypeGroup.ViewTypeFloorPlan; resultType = ViewType.FloorPlan;
                    break;
                case "structural_plan":
                    fields = RangedPlanFields; required = new[] { "level" };
                    family = ViewFamily.StructuralPlan; group = ElementTypeGroup.ViewTypeStructuralPlan; resultType = ViewType.EngineeringPlan;
                    break;
                case "drafting":
                    fields = new string[0]; required = fields;
                    family = ViewFamily.Drafting; group = ElementTypeGroup.ViewTypeDrafting; resultType = ViewType.DraftingView;
                    break;
                case "ceiling_plan":
                    fields = PlanFields; required = new[] { "level" };
                    family = ViewFamily.CeilingPlan; group = ElementTypeGroup.ViewTypeCeilingPlan; resultType = ViewType.CeilingPlan;
                    break;
                case "3d":
                    fields = new string[0]; required = fields;
                    family = ViewFamily.ThreeDimensional; group = ElementTypeGroup.ViewType3D; resultType = ViewType.ThreeD;
                    break;
                case "section":
                    fields = SectionFields; required = SectionRequired;
                    family = ViewFamily.Section; group = ElementTypeGroup.ViewTypeSection; resultType = ViewType.Section;
                    break;
                default:
                    fields = ElevationFields; required = ElevationFields;
                    family = ViewFamily.Elevation; group = ElementTypeGroup.ViewTypeElevation; resultType = ViewType.Elevation;
                    break;
            }

            var stray = p.Properties().Select(x => x.Name).Where(n => !Common.Contains(n) && !fields.Contains(n)).ToList();
            if (stray.Count > 0)
                return CommandResult.Fail("INVALID_PARAMS: " + string.Join(", ", stray) + " do(es) not apply to viewType " + kind
                    + ". Fields for " + kind + ": " + string.Join(", ", Common.Concat(fields)) + ".");
            var missing = required.Where(n => p[n] == null || p[n].Type == JTokenType.Null).ToList();
            if (missing.Count > 0)
                return CommandResult.Fail("INVALID_PARAMS: viewType " + kind + " needs " + string.Join(", ", missing) + ".");

            var scale = p.Value<int?>("scale");
            if (scale.HasValue && (scale.Value < 1 || scale.Value > 24000))
                return CommandResult.Fail("INVALID_PARAMS: scale is the denominator of 1:N and must be 1 to 24000.");

            var name = p.Value<string>("name");
            if (!string.IsNullOrEmpty(name))
            {
                var clash = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(v => v.ViewType == resultType && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
                if (clash != null)
                    return CommandResult.Fail("NAME_IN_USE: a " + resultType + " view named '" + clash.Name + "' already exists.",
                        new { existingViewId = RevitCompat.GetId(clash.Id) });
            }

            if (!Lookup.TryViewFamilyType(doc, family, group, p.Value<string>("viewFamilyTypeName"), out var vft, out var failure))
                return failure;
            Level level = null;
            if (p["level"] != null && !Lookup.TryLevel(doc, p.Value<string>("level"), out level, out failure)) return failure;

            // Geometry checks before the transaction.
            double[] crop = null;
            var cropGiven = PlanFields.Skip(1).Count(n => p[n] != null);
            if (cropGiven > 0)
            {
                if (cropGiven < 4) return CommandResult.Fail("INVALID_PARAMS: give all of cropMinX, cropMinY, cropMaxX, cropMaxY, or none.");
                crop = new[] { p.Value<double>("cropMinX"), p.Value<double>("cropMinY"), p.Value<double>("cropMaxX"), p.Value<double>("cropMaxY") };
                if (crop[0] >= crop[2] || crop[1] >= crop[3])
                    return CommandResult.Fail("INVALID_PARAMS: cropMinX/cropMinY must be smaller than cropMaxX/cropMaxY.");
            }

            View template = null;
            var templateName = p.Value<string>("viewTemplateName");
            if (!string.IsNullOrEmpty(templateName))
            {
                var templates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => v.IsTemplate && v.ViewType == resultType).OrderBy(v => v.Name).ToList();
                template = templates.FirstOrDefault(v => string.Equals(v.Name, templateName, StringComparison.OrdinalIgnoreCase));
                if (template == null)
                    return CommandResult.Fail("TEMPLATE_NOT_FOUND: no " + resultType + " view template named '" + templateName + "'.",
                        new JObject { ["valid_view_templates"] = new JArray(templates.Take(100).Select(v => v.Name)) });
            }

            List<RangePlane> viewRange = null;
            if (p["viewRange"] != null && p["viewRange"].Type != JTokenType.Null)
            {
                if (!TryViewRange(doc, p["viewRange"], level, out viewRange, out failure)) return failure;
                if (template != null && TemplateControlsViewRange(template))
                    return CommandResult.Fail("INVALID_PARAMS: view template '" + template.Name + "' controls the view range, so viewRange would "
                        + "not hold; leave viewRange out or use a template that does not include View Range.");
            }

            BoundingBoxXYZ sectionBox = null;
            XYZ sectionStart = null, sectionEnd = null;
            if (kind == "section")
            {
                if (!TryHeight(doc, p, "bottom", out var bottomMm, out failure)) return failure;
                if (!TryHeight(doc, p, "top", out var topMm, out failure)) return failure;
                var start = new XYZ(Mm.ToFeet(p.Value<double>("startX")), Mm.ToFeet(p.Value<double>("startY")), 0);
                var end = new XYZ(Mm.ToFeet(p.Value<double>("endX")), Mm.ToFeet(p.Value<double>("endY")), 0);
                var depth = Mm.ToFeet(p.Value<double>("depth"));
                var bottom = Mm.ToFeet(bottomMm);
                var top = Mm.ToFeet(topMm);
                if (start.DistanceTo(end) <= app.Application.ShortCurveTolerance)
                    return CommandResult.Fail("LINE_TOO_SHORT: the section line start and end are closer than Revit's shortest allowed line.");
                if (depth <= 0) return CommandResult.Fail("INVALID_PARAMS: depth must be greater than 0 mm.");
                if (top <= bottom) return CommandResult.Fail("INVALID_PARAMS: top must be above bottom.");
                sectionBox = SectionBox(start, end, depth, bottom, top, p.Value<string>("side") == "right");
                sectionStart = start;
                sectionEnd = end;
            }

            XYZ looking = null;
            ViewPlan hostPlan = null;
            if (kind == "elevation")
            {
                looking = Compass(p.Value<string>("direction"));
                hostPlan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel != null && v.GenLevel.Id == level.Id)
                    .OrderBy(v => v.Name).FirstOrDefault();
                if (hostPlan == null)
                    return CommandResult.Fail("NO_PLAN_VIEW: level '" + level.Name + "' has no floor plan to host the elevation marker; "
                        + "create one with viewType floor_plan first.");
            }

            using (var tx = new Transaction(doc, "B2M: Create View"))
            {
                tx.Start();
                try
                {
                    View view;
                    ElevationMarker marker = null;
                    switch (kind)
                    {
                        case "floor_plan":
                        case "structural_plan":
                        case "ceiling_plan":
                            view = ViewPlan.Create(doc, vft.Id, level.Id);
                            break;
                        case "drafting":
                            view = ViewDrafting.Create(doc, vft.Id);
                            break;
                        case "3d":
                            view = View3D.CreateIsometric(doc, vft.Id);
                            break;
                        case "section":
                            view = ViewSection.CreateSection(doc, vft.Id, sectionBox);
                            break;
                        default:
                            var origin = new XYZ(Mm.ToFeet(p.Value<double>("x")), Mm.ToFeet(p.Value<double>("y")), level.ProjectElevation);
                            marker = ElevationMarker.CreateElevationMarker(doc, vft.Id, origin, scale ?? 100);
                            view = ElevationFacing(doc, marker, hostPlan, looking);
                            break;
                    }
                    if (view == null) throw new InvalidOperationException("Revit returned no view.");

                    if (!string.IsNullOrEmpty(name)) view.Name = name;
                    if (scale.HasValue) view.Scale = scale.Value;
                    var detail = p.Value<string>("detailLevel");
                    if (detail != null)
                        view.DetailLevel = detail == "coarse" ? ViewDetailLevel.Coarse : detail == "medium" ? ViewDetailLevel.Medium : ViewDetailLevel.Fine;
                    if (crop != null) ApplyCrop(view, crop);
                    if (viewRange != null) ApplyViewRange(doc, (ViewPlan)view, level, viewRange);
                    if (template != null)
                    {
                        if (!view.IsValidViewTemplate(template.Id))
                            throw new InvalidOperationException("view template '" + template.Name + "' cannot be applied to this view.");
                        view.ViewTemplateId = template.Id;
                    }
                    object sectionLine = null;
                    if (kind == "section")
                    {
                        doc.Regenerate();
                        var deviation = Math.Max(OffPlaneMm(view, sectionStart), OffPlaneMm(view, sectionEnd));
                        var far = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
                        sectionLine = new
                        {
                            startX = Mm.FromFeet(sectionStart.X), startY = Mm.FromFeet(sectionStart.Y),
                            endX = Mm.FromFeet(sectionEnd.X), endY = Mm.FromFeet(sectionEnd.Y),
                            cutPlaneDeviationMm = Math.Round(deviation, 3),
                            farClipMm = far != null && far.HasValue ? Mm.FromFeet(far.AsDouble()) : (double?)null
                        };
                        if (deviation > 1.0)
                            throw new InvalidOperationException("the section's cut plane is " + Math.Round(deviation, 1)
                                + " mm off the given line.");
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    var appliedTemplate = doc.GetElement(view.ViewTemplateId) as View;
                    var look = view.ViewDirection.Negate();
                    return CommandResult.Ok(new
                    {
                        viewId = RevitCompat.GetId(view.Id),
                        viewName = view.Name,
                        viewType = view.ViewType.ToString(),
                        viewFamilyType = vft.Name,
                        levelName = view.GenLevel?.Name ?? level?.Name,
                        scale = view.Scale,
                        detailLevel = view.DetailLevel.ToString(),
                        viewTemplate = appliedTemplate?.Name,
                        cropMm = crop == null ? null : CropMm(view),
                        looksToward = kind == "section" || kind == "elevation"
                            ? new[] { Math.Round(look.X, 6), Math.Round(look.Y, 6), Math.Round(look.Z, 6) } : null,
                        elevationMarkerId = marker == null ? (long?)null : RevitCompat.GetId(marker.Id),
                        sectionLineMm = sectionLine,
                        viewRangeMm = kind == "floor_plan" || kind == "structural_plan" ? ViewRangeMm(doc, (ViewPlan)view) : null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the " + kind + " view: " + ex.Message + " Nothing was created.");
                }
            }
        }

        /// <summary>One view range plane the request sets: its level and its offset from that level.</summary>
        private sealed class RangePlane
        {
            public PlanViewPlane Plane;
            public string Name;
            public Level Level;
            public double OffsetMm;

            public double AbsoluteMm => Mm.FromFeet(Level.ProjectElevation, 3) + OffsetMm;
        }

        private static readonly PlanViewPlane[] RangePlanes =
            { PlanViewPlane.TopClipPlane, PlanViewPlane.CutPlane, PlanViewPlane.BottomClipPlane, PlanViewPlane.ViewDepthPlane };

        /// <summary>
        /// Parses viewRange: {plane}_offset_mm from the view's level or from {plane}_level (top, bottom, depth; the cut plane
        /// is always on the view's level). A plane is set when its offset or level is given. The given planes must already
        /// run top >= cut >= bottom >= depth.
        /// </summary>
        private static bool TryViewRange(Document doc, JToken token, Level viewLevel, out List<RangePlane> planes, out CommandResult failure)
        {
            planes = null;
            failure = null;
            var o = token as JObject;
            var stray = o?.Properties().Select(x => x.Name).Where(n => !ViewRangeFields.Contains(n)).ToList() ?? new List<string>();
            if (o == null || stray.Count > 0 || o.Count == 0)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: viewRange must be an object with any of " + string.Join(", ", ViewRangeFields)
                    + (stray.Count > 0 ? "; unknown field(s) " + string.Join(", ", stray) : "") + ".");
                return false;
            }
            var result = new List<RangePlane>();
            for (var i = 0; i < RangePlanes.Length; i++)
            {
                var name = ViewRangeRules.Planes[i];
                var offsetToken = o[name + "_offset_mm"];
                var levelToken = o[name + "_level"];
                if ((offsetToken == null || offsetToken.Type == JTokenType.Null) && (levelToken == null || levelToken.Type == JTokenType.Null)) continue;
                double offset = 0;
                if (offsetToken != null && offsetToken.Type != JTokenType.Null)
                {
                    if (offsetToken.Type != JTokenType.Integer && offsetToken.Type != JTokenType.Float)
                    {
                        failure = CommandResult.Fail("INVALID_PARAMS: viewRange." + name + "_offset_mm must be a number (mm).");
                        return false;
                    }
                    offset = offsetToken.Value<double>();
                }
                var planeLevel = viewLevel;
                if (levelToken != null && levelToken.Type != JTokenType.Null)
                {
                    if (levelToken.Type != JTokenType.String)
                    {
                        failure = CommandResult.Fail("INVALID_PARAMS: viewRange." + name + "_level must be a level name.");
                        return false;
                    }
                    if (!Lookup.TryLevel(doc, levelToken.Value<string>(), out planeLevel, out failure)) return false;
                }
                result.Add(new RangePlane { Plane = RangePlanes[i], Name = name, Level = planeLevel, OffsetMm = offset });
            }
            double? At(string n) => result.FirstOrDefault(r => r.Name == n)?.AbsoluteMm;
            var reason = ViewRangeRules.Validate(At("top"), At("cut"), At("bottom"), At("depth"));
            if (reason != null)
            {
                failure = CommandResult.Fail("INVALID_VIEW_RANGE: " + reason + " Heights are absolute (level elevation + offset, mm).",
                    new JObject { ["given_mm"] = new JObject(result.Select(r => new JProperty(r.Name, Math.Round(r.AbsoluteMm, 1)))) });
                return false;
            }
            planes = result;
            return true;
        }

        private static bool TemplateControlsViewRange(View template)
        {
            var rangeId = new ElementId(BuiltInParameter.PLAN_VIEW_RANGE);
            return template.GetTemplateParameterIds().Contains(rangeId) && !template.GetNonControlledTemplateParameterIds().Contains(rangeId);
        }

        /// <summary>The level a view range plane is on (resolving "current level" to the view's level), or null when not a level.</summary>
        private static Level PlaneLevel(Document doc, ViewPlan view, PlanViewRange range, PlanViewPlane plane)
        {
            var id = range.GetLevelId(plane);
            if (id == PlanViewRange.Current) return view.GenLevel;
            return doc.GetElement(id) as Level;
        }

        /// <summary>
        /// Inside the transaction, on the new view only: sets the given planes on the view's own range, checks the merged
        /// range (unlimited or relative planes are not compared) and applies it. Throws when it is not valid.
        /// </summary>
        private static void ApplyViewRange(Document doc, ViewPlan view, Level viewLevel, List<RangePlane> planes)
        {
            var range = view.GetViewRange();
            foreach (var plane in planes)
            {
                range.SetLevelId(plane.Plane, plane.Plane == PlanViewPlane.CutPlane ? viewLevel.Id : plane.Level.Id);
                range.SetOffset(plane.Plane, Mm.ToFeet(plane.OffsetMm));
            }
            var heights = RangePlanes.Select(pl =>
            {
                var level = PlaneLevel(doc, view, range, pl);
                return level == null ? (double?)null : DrawingMm(level.ProjectElevation + range.GetOffset(pl));
            }).ToArray();
            var reason = ViewRangeRules.Validate(heights[0], heights[1], heights[2], heights[3]);
            if (reason != null)
                throw new InvalidOperationException("with the view type's default planes the view range is not valid: " + reason);
            view.SetViewRange(range);
        }

        private static double DrawingMm(double feet) => feet * 304.8;

        /// <summary>The plan's view range as {plane: {level, offset_mm}} (level null for unlimited or relative planes).</summary>
        private static JObject ViewRangeMm(Document doc, ViewPlan view)
        {
            PlanViewRange range;
            try { range = view.GetViewRange(); }
            catch (Exception) { return null; }
            var result = new JObject();
            for (var i = 0; i < RangePlanes.Length; i++)
            {
                var level = PlaneLevel(doc, view, range, RangePlanes[i]);
                result[ViewRangeRules.Planes[i]] = new JObject
                {
                    ["level"] = level?.Name,
                    ["offset_mm"] = Mm.FromFeet(range.GetOffset(RangePlanes[i]))
                };
            }
            return result;
        }

        /// <summary>
        /// Section box with its origin on the plan line's midpoint at height 0: BasisX along the line (reversed when
        /// looking right), BasisY up, BasisZ (toward the viewer) = BasisX x up, so the view looks to the left of
        /// start->end, or to the right when lookRight. Local Y is therefore absolute height; local Z runs from -depth
        /// (far clip) to 0 (the cut, on the line).
        /// </summary>
        private static BoundingBoxXYZ SectionBox(XYZ start, XYZ end, double depth, double bottom, double top, bool lookRight)
        {
            var along = (end - start).Normalize();
            if (lookRight) along = along.Negate();
            var transform = Transform.Identity;
            transform.Origin = (start + end) * 0.5;
            transform.BasisX = along;
            transform.BasisY = XYZ.BasisZ;
            transform.BasisZ = along.CrossProduct(XYZ.BasisZ);
            var half = start.DistanceTo(end) * 0.5;
            return new BoundingBoxXYZ
            {
                Transform = transform,
                Min = new XYZ(-half, bottom, -depth),
                Max = new XYZ(half, top, 0)
            };
        }

        /// <summary>
        /// bottom/top: an absolute height in mm, or a level's elevation plus an offset (bottomLevel + bottomOffset).
        /// Exactly one form must be given.
        /// </summary>
        private static bool TryHeight(Document doc, JObject p, string which, out double mm, out CommandResult failure)
        {
            mm = 0;
            failure = null;
            var absolute = p[which];
            var levelName = p.Value<string>(which + "Level");
            var offset = p[which + "Offset"];
            if (absolute != null && levelName != null)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: give " + which + " or " + which + "Level (+ " + which + "Offset), not both.");
                return false;
            }
            if (absolute != null)
            {
                if (offset != null)
                {
                    failure = CommandResult.Fail("INVALID_PARAMS: " + which + "Offset goes with " + which + "Level, not with " + which + ".");
                    return false;
                }
                mm = absolute.Value<double>();
                return true;
            }
            if (levelName == null)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: viewType section needs " + which + " (mm) or " + which + "Level.");
                return false;
            }
            if (!Lookup.TryLevel(doc, levelName, out var level, out failure)) return false;
            mm = Mm.FromFeet(level.ProjectElevation, 3) + (offset?.Value<double>() ?? 0);
            return true;
        }

        /// <summary>How far (mm) a plan point is from the section's cut plane: the crop box's front face (local Z = Max.Z).</summary>
        private static double OffPlaneMm(View view, XYZ point)
        {
            var box = view.CropBox;
            var t = box.Transform;
            var front = t.OfPoint(new XYZ(0, 0, box.Max.Z));
            var p = new XYZ(point.X, point.Y, front.Z);
            return Math.Abs((p - front).DotProduct(t.BasisZ)) * 304.8;
        }

        private static XYZ Compass(string direction)
        {
            switch (direction)
            {
                case "north": return XYZ.BasisY;
                case "south": return XYZ.BasisY.Negate();
                case "east": return XYZ.BasisX;
                default: return XYZ.BasisX.Negate();
            }
        }

        /// <summary>
        /// The marker index decides the view direction, and the API does not document which index faces
        /// which way, so each free index is tried and kept only when the view looks the requested way.
        /// </summary>
        private static View ElevationFacing(Document doc, ElevationMarker marker, ViewPlan plan, XYZ looking)
        {
            for (var i = 0; i < marker.MaximumViewCount; i++)
            {
                if (!marker.IsAvailableIndex(i)) continue;
                var view = marker.CreateElevation(doc, plan.Id, i);
                if (view.ViewDirection.Negate().DotProduct(looking) > 0.999) return view;
                doc.Delete(view.Id);
            }
            throw new InvalidOperationException("no index on the elevation marker looks that way.");
        }

        private static void ApplyCrop(View view, IReadOnlyList<double> crop)
        {
            view.CropBoxActive = true;
            var box = view.CropBox;
            var toLocal = box.Transform.Inverse;
            var a = toLocal.OfPoint(new XYZ(Mm.ToFeet(crop[0]), Mm.ToFeet(crop[1]), 0));
            var b = toLocal.OfPoint(new XYZ(Mm.ToFeet(crop[2]), Mm.ToFeet(crop[3]), 0));
            box.Min = new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), box.Min.Z);
            box.Max = new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), box.Max.Z);
            view.CropBox = box;
        }

        private static object CropMm(View view)
        {
            var box = view.CropBox;
            var a = box.Transform.OfPoint(box.Min);
            var b = box.Transform.OfPoint(box.Max);
            return new
            {
                minX = Mm.FromFeet(Math.Min(a.X, b.X)), minY = Mm.FromFeet(Math.Min(a.Y, b.Y)),
                maxX = Mm.FromFeet(Math.Max(a.X, b.X)), maxY = Mm.FromFeet(Math.Max(a.Y, b.Y))
            };
        }
    }
}
