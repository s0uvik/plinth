// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Annotations/CreateFilledRegionHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: in plan views the points are model X/Y in mm (other views: mm along the view's right/up from
// its origin, as before); with no filled_region_type_id the project's default filled region type is used and a
// miss lists the valid types (rvt-mcp took the first type found); each point is checked here because the schema
// checker stops at the top level; points closer than Revit's short-curve tolerance are an error; mm via Mm.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Annotation
{
    // B2M.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class CreateFilledRegionHandler : IRevitCommand
    {
        public string Name => "create_filled_region";
        public string Description =>
            "Create a filled region in a 2D view (view_id, default the active view) from at least 3 points [{x, y}] (do not repeat "
            + "the first point). Plan views: model coordinates in mm; other views: mm along the view's right/up from its origin. "
            + "Optional filled_region_type_id (default: project default).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""points"":{""type"":""array"",""items"":{""type"":""object""}},
            ""view_id"":{""type"":""integer""},
            ""filled_region_type_id"":{""type"":""integer""}},
            ""required"":[""points""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var points = (JArray)p["points"];
            if (points.Count < 3) return CommandResult.Fail("INVALID_PARAMS: a filled region needs at least 3 points.");
            if (!AnnotationHelpers.TryAnnotationView(doc, p.Value<long?>("view_id"), out View view, out var failure)) return failure;
            if (view.ViewType == ViewType.ThreeD)
                return CommandResult.Fail("VIEW_NOT_ANNOTATABLE: filled regions cannot be drawn in a 3D view.");

            var typeId = p.Value<long?>("filled_region_type_id");
            var regionType = typeId.HasValue
                ? doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as FilledRegionType
                : doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.FilledRegionType)) as FilledRegionType;
            if (regionType == null)
                return CommandResult.Fail("TYPE_NOT_FOUND: " + (typeId.HasValue
                        ? "no filled region type has id " + typeId.Value + "."
                        : "the project has no default filled region type; pass filled_region_type_id."),
                    new JObject
                    {
                        ["valid_filled_region_types"] = new JArray(new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType))
                            .Cast<FilledRegionType>().OrderBy(t => t.Name).Take(100)
                            .Select(t => new JObject { ["id"] = RevitCompat.GetId(t.Id), ["name"] = t.Name }))
                    });

            var xyz = new List<XYZ>();
            for (var i = 0; i < points.Count; i++)
            {
                if (!AnnotationHelpers.TryXy(points[i], "points[" + i + "]", out var x, out var y, out failure)) return failure;
                xyz.Add(AnnotationHelpers.ViewPoint(view, x, y));
            }
            var shortCurve = app.Application.ShortCurveTolerance;
            for (var i = 0; i < xyz.Count; i++)
                if (xyz[i].DistanceTo(xyz[(i + 1) % xyz.Count]) <= shortCurve)
                    return CommandResult.Fail("INVALID_OUTLINE: points " + i + " and " + ((i + 1) % xyz.Count)
                        + " are too close; remove repeated points (do not repeat the first point at the end).");

            FilledRegion region;
            using (var tx = new Transaction(doc, "B2M: Create Filled Region"))
            {
                tx.Start();
                try
                {
                    var loop = new CurveLoop();
                    for (var i = 0; i < xyz.Count; i++) loop.Append(Line.CreateBound(xyz[i], xyz[(i + 1) % xyz.Count]));
                    region = FilledRegion.Create(doc, regionType.Id, view.Id, new List<CurveLoop> { loop });
                    if (region == null) throw new InvalidOperationException("Revit returned no filled region.");
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the filled region: " + ex.Message + " Nothing was created.");
                }
            }

            // Shoelace area in the view plane, square feet to square metres.
            double area = 0;
            for (var i = 0; i < xyz.Count; i++)
            {
                var a = xyz[i];
                var b = xyz[(i + 1) % xyz.Count];
                area += a.DotProduct(view.RightDirection) * b.DotProduct(view.UpDirection)
                    - b.DotProduct(view.RightDirection) * a.DotProduct(view.UpDirection);
            }
            var areaM2 = Math.Abs(area) * 0.5 * Math.Pow(Mm.FromFeet(1, 6) / 1000.0, 2);

            return CommandResult.Ok(new JObject
            {
                ["filled_region_id"] = RevitCompat.GetId(region.Id),
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["filled_region_type_id"] = RevitCompat.GetId(region.GetTypeId()),
                ["filled_region_type_name"] = regionType.Name,
                ["point_count"] = xyz.Count,
                ["area_estimate_m2"] = Math.Round(areaM2, 3)
            });
        }
    }
}
