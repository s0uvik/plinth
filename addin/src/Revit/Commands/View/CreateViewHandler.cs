// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Views/CreateViewHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M (PRD: drawing set views): adds ceiling plans, building sections (a plan line in mm,
// far-clip depth and bottom/top heights build the section box) and elevations (an ElevationMarker at a
// point on the level's floor plan, looking north/south/east/west); optional name, view type name,
// scale, detail level and a plan crop rectangle in mm; an unknown level is an error with the valid
// options (rvt-mcp fell back to the lowest level); a duplicate view name fails before anything is created;
// fields that do not apply to the chosen view type are rejected.

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

    public sealed class CreateViewHandler : IRevitCommand
    {
        private static readonly string[] Common = { "viewType", "name", "viewFamilyTypeName", "scale", "detailLevel" };
        private static readonly string[] PlanFields = { "level", "cropMinX", "cropMinY", "cropMaxX", "cropMaxY" };
        private static readonly string[] SectionFields = { "startX", "startY", "endX", "endY", "depth", "bottom", "top" };
        private static readonly string[] ElevationFields = { "level", "x", "y", "direction" };

        public string Name => "create_view";
        public string Description =>
            "Create a view. floor_plan / ceiling_plan: on a named level, optional crop rectangle (cropMinX..cropMaxY, mm). "
            + "3d: isometric. section: plan line startX/startY to endX/endY (mm); the section looks to the left of start->end "
            + "(drawn west to east it looks north) for depth mm; bottom/top are absolute heights in mm. "
            + "elevation: marker at x/y (mm) on the named level's floor plan; direction is the way the view looks (north = +Y). "
            + "Optional name, viewFamilyTypeName, scale (100 for 1:100), detailLevel.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""viewType"":{""type"":""string"",""enum"":[""floor_plan"",""ceiling_plan"",""3d"",""section"",""elevation""]},
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
                    fields = PlanFields; required = new[] { "level" };
                    family = ViewFamily.FloorPlan; group = ElementTypeGroup.ViewTypeFloorPlan; resultType = ViewType.FloorPlan;
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
                    fields = SectionFields; required = SectionFields;
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

            BoundingBoxXYZ sectionBox = null;
            if (kind == "section")
            {
                var start = new XYZ(Mm.ToFeet(p.Value<double>("startX")), Mm.ToFeet(p.Value<double>("startY")), 0);
                var end = new XYZ(Mm.ToFeet(p.Value<double>("endX")), Mm.ToFeet(p.Value<double>("endY")), 0);
                var depth = Mm.ToFeet(p.Value<double>("depth"));
                var bottom = Mm.ToFeet(p.Value<double>("bottom"));
                var top = Mm.ToFeet(p.Value<double>("top"));
                if (start.DistanceTo(end) <= app.Application.ShortCurveTolerance)
                    return CommandResult.Fail("LINE_TOO_SHORT: the section line start and end are closer than Revit's shortest allowed line.");
                if (depth <= 0) return CommandResult.Fail("INVALID_PARAMS: depth must be greater than 0 mm.");
                if (top <= bottom) return CommandResult.Fail("INVALID_PARAMS: top must be above bottom.");
                sectionBox = SectionBox(start, end, depth, bottom, top);
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
                        case "ceiling_plan":
                            view = ViewPlan.Create(doc, vft.Id, level.Id);
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

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    var template = doc.GetElement(view.ViewTemplateId) as View;
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
                        viewTemplate = template?.Name,
                        cropMm = crop == null ? null : CropMm(view),
                        looksToward = kind == "section" || kind == "elevation"
                            ? new[] { Math.Round(look.X, 6), Math.Round(look.Y, 6), Math.Round(look.Z, 6) } : null,
                        elevationMarkerId = marker == null ? (long?)null : RevitCompat.GetId(marker.Id)
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the " + kind + " view: " + ex.Message + " Nothing was created.");
                }
            }
        }

        /// <summary>
        /// Section box with its origin on the plan line's midpoint at height 0: BasisX along the line,
        /// BasisY up, BasisZ (toward the viewer) = line x up, so the view looks to the left of start->end.
        /// Local Y is therefore absolute height; local Z runs from -depth (far clip) to 0 (the cut).
        /// </summary>
        private static BoundingBoxXYZ SectionBox(XYZ start, XYZ end, double depth, double bottom, double top)
        {
            var along = (end - start).Normalize();
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
