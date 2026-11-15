// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Annotations/CreateDetailLineHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: in plan views the points are model X/Y in mm (other views: mm along the view's right/up from
// its origin, as before); line_style_id is checked before the transaction and must be a line style (a subcategory
// of Lines), a miss lists the valid styles; the minimum length is Revit's short-curve tolerance; mm via Mm.

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

    public sealed class CreateDetailLineHandler : IRevitCommand
    {
        public string Name => "create_detail_line";
        public string Description =>
            "Create a detail line in a 2D view (view_id, default the active view) from start_x/start_y to end_x/end_y. "
            + "Plan views: model coordinates in mm; other views: mm along the view's right/up from its origin. Optional line_style_id.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""start_x"":{""type"":""number""},
            ""start_y"":{""type"":""number""},
            ""end_x"":{""type"":""number""},
            ""end_y"":{""type"":""number""},
            ""view_id"":{""type"":""integer""},
            ""line_style_id"":{""type"":""integer""}},
            ""required"":[""start_x"",""start_y"",""end_x"",""end_y""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            if (!AnnotationHelpers.TryAnnotationView(doc, p.Value<long?>("view_id"), out View view, out var failure)) return failure;
            if (view.ViewType == ViewType.ThreeD)
                return CommandResult.Fail("VIEW_NOT_ANNOTATABLE: detail lines cannot be drawn in a 3D view.");

            GraphicsStyle style = null;
            var styleId = p.Value<long?>("line_style_id");
            if (styleId.HasValue)
            {
                var valid = LineStyles(doc);
                style = valid.FirstOrDefault(s => RevitCompat.GetId(s.Id) == styleId.Value);
                if (style == null)
                    return CommandResult.Fail("LINE_STYLE_NOT_FOUND: no line style has id " + styleId.Value + ".",
                        new JObject { ["valid_line_styles"] = new JArray(valid.Take(100).Select(s => new JObject { ["id"] = RevitCompat.GetId(s.Id), ["name"] = s.Name })) });
            }

            var start = AnnotationHelpers.ViewPoint(view, p.Value<double>("start_x"), p.Value<double>("start_y"));
            var end = AnnotationHelpers.ViewPoint(view, p.Value<double>("end_x"), p.Value<double>("end_y"));
            if (start.DistanceTo(end) <= app.Application.ShortCurveTolerance)
                return CommandResult.Fail("LINE_TOO_SHORT: start and end are closer than Revit's shortest allowed line.");

            DetailCurve curve;
            using (var tx = new Transaction(doc, "B2M: Create Detail Line"))
            {
                tx.Start();
                try
                {
                    curve = doc.Create.NewDetailCurve(view, Line.CreateBound(start, end));
                    if (curve == null) throw new InvalidOperationException("Revit returned no detail line.");
                    if (style != null) curve.LineStyle = style;
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the detail line: " + ex.Message + " Nothing was created.");
                }
            }

            var geometry = curve.GeometryCurve;
            return CommandResult.Ok(new JObject
            {
                ["detail_line_id"] = RevitCompat.GetId(curve.Id),
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["line_style_id"] = RevitCompat.GetId(curve.LineStyle.Id),
                ["line_style_name"] = curve.LineStyle.Name,
                ["start_mm"] = AnnotationHelpers.PointMm(geometry.GetEndPoint(0)),
                ["end_mm"] = AnnotationHelpers.PointMm(geometry.GetEndPoint(1)),
                ["length_mm"] = Mm.FromFeet(geometry.Length)
            });
        }

        /// <summary>Projection graphics styles of the Lines category's subcategories: the line styles a detail line accepts.</summary>
        private static List<GraphicsStyle> LineStyles(Document doc)
        {
            var lines = Category.GetCategory(doc, BuiltInCategory.OST_Lines);
            var result = new List<GraphicsStyle>();
            if (lines == null) return result;
            foreach (Category sub in lines.SubCategories)
            {
                var gs = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
                if (gs != null) result.Add(gs);
            }
            return result.OrderBy(s => s.Name).ToList();
        }
    }
}
