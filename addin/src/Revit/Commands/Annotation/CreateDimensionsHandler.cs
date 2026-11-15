// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Annotations/CreateDimensionsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: every reference must resolve or nothing is created (rvt-mcp dimensioned the ones that
// resolved, and returned success with created=false when fewer than two did); the nested references and line
// objects are checked here because the schema checker stops at the top level; a bad dimension_type_id lists the
// valid linear dimension types; a line given with coincident ends is an error instead of being replaced by the
// inferred one; mm via Mm.

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

    public sealed class CreateDimensionsHandler : IRevitCommand
    {
        private static readonly string[] Modes = { "wall_centerline", "grid_curve", "explicit_reference_stable" };

        public string Name => "create_dimensions";
        public string Description =>
            "Create one dimension string in a view (view_id, default the active view) through two or more references, each "
            + "{mode, element_id, stable_reference?}: mode wall_centerline (a wall), grid_curve (a grid) or explicit_reference_stable "
            + "(a stable reference string). line {start:{x,y,z}, end:{x,y,z}} in mm places the dimension line; without it the line "
            + "runs between the first two elements' centres, 304.8 mm to one side. Optional dimension_type_id.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""references"":{""type"":""array"",""items"":{""type"":""object""}},
            ""view_id"":{""type"":""integer""},
            ""dimension_type_id"":{""type"":""integer""},
            ""line"":{""type"":""object""}},
            ""required"":[""references""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var items = (JArray)p["references"];
            if (items.Count < 2) return CommandResult.Fail("INVALID_PARAMS: a dimension needs at least two references.");
            if (!AnnotationHelpers.TryAnnotationView(doc, p.Value<long?>("view_id"), out View view, out var failure)) return failure;

            DimensionType dimType = null;
            var dimTypeId = p.Value<long?>("dimension_type_id");
            if (dimTypeId.HasValue)
            {
                dimType = doc.GetElement(RevitCompat.ToElementId(dimTypeId.Value)) as DimensionType;
                if (dimType == null)
                    return CommandResult.Fail("TYPE_NOT_FOUND: no dimension type has id " + dimTypeId.Value + ".", new JObject
                    {
                        ["valid_dimension_types"] = new JArray(new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                            .Where(t => t.StyleType == DimensionStyleType.Linear).OrderBy(t => t.Name).Take(100)
                            .Select(t => new JObject { ["id"] = RevitCompat.GetId(t.Id), ["name"] = t.Name }))
                    });
            }

            Line line = null;
            if (p["line"] != null && p["line"].Type != JTokenType.Null)
            {
                if (!TryPointMm(p["line"]["start"], "line.start", out var a, out failure)) return failure;
                if (!TryPointMm(p["line"]["end"], "line.end", out var b, out failure)) return failure;
                if (a.DistanceTo(b) <= app.Application.ShortCurveTolerance)
                    return CommandResult.Fail("LINE_TOO_SHORT: line.start and line.end are closer than Revit's shortest allowed line.");
                line = Line.CreateBound(a, b);
            }

            var references = new ReferenceArray();
            var elements = new List<Element>();
            var resolved = new JArray();
            var failed = new JArray();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i] as JObject;
                var mode = item?.Value<string>("mode");
                var idToken = item?["element_id"];
                if (item == null || !Modes.Contains(mode) || idToken == null || idToken.Type != JTokenType.Integer)
                    return CommandResult.Fail("INVALID_PARAMS: references[" + i + "] must be {\"mode\": one of "
                        + string.Join(", ", Modes) + ", \"element_id\": integer, \"stable_reference\"?: string}.");
                var elementId = idToken.Value<long>();
                try
                {
                    var element = doc.GetElement(RevitCompat.ToElementId(elementId));
                    if (element == null) throw new InvalidOperationException("element not found.");
                    references.Append(Resolve(doc, element, mode, item.Value<string>("stable_reference")));
                    elements.Add(element);
                    resolved.Add(new JObject { ["mode"] = mode, ["element_id"] = elementId });
                }
                catch (Exception ex)
                {
                    failed.Add(new JObject { ["index"] = i, ["mode"] = mode, ["element_id"] = elementId, ["error"] = ex.Message });
                }
            }
            if (failed.Count > 0)
                return CommandResult.Fail("REFERENCE_NOT_RESOLVED: " + failed.Count + " reference(s) could not be resolved; see failed_references. Nothing was created.",
                    new JObject { ["resolved_references"] = resolved, ["failed_references"] = failed });

            if (line == null) line = InferLine(view, elements[0], elements[1]);
            if (line == null)
                return CommandResult.Fail("LINE_REQUIRED: could not infer a dimension line from the first two elements; pass line.");

            Dimension dimension;
            using (var tx = new Transaction(doc, "B2M: Create Dimension"))
            {
                tx.Start();
                try
                {
                    dimension = doc.Create.NewDimension(view, line, references);
                    if (dimension == null) throw new InvalidOperationException("Revit returned no dimension.");
                    if (dimType != null) dimension.ChangeTypeId(dimType.Id);
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the dimension: " + ex.Message + " Nothing was created.");
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["dimension_id"] = RevitCompat.GetId(dimension.Id),
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["dimension_type_id"] = RevitCompat.GetId(dimension.GetTypeId()),
                ["reference_count"] = references.Size,
                ["value_mm"] = dimension.Value.HasValue ? Mm.FromFeet(dimension.Value.Value) : (double?)null,
                ["line_mm"] = new JObject
                {
                    ["start"] = AnnotationHelpers.PointMm(line.GetEndPoint(0)),
                    ["end"] = AnnotationHelpers.PointMm(line.GetEndPoint(1))
                },
                ["resolved_references"] = resolved
            });
        }

        private static Reference Resolve(Document doc, Element element, string mode, string stable)
        {
            switch (mode)
            {
                case "explicit_reference_stable":
                    if (string.IsNullOrEmpty(stable)) throw new InvalidOperationException("stable_reference is required for explicit_reference_stable.");
                    return Reference.ParseFromStableRepresentation(doc, stable)
                        ?? throw new InvalidOperationException("stable_reference did not parse.");
                case "grid_curve":
                    if (!(element is Grid grid)) throw new InvalidOperationException("element is not a grid.");
                    return new Reference(grid);
                default:
                    if (!(element is Wall wall)) throw new InvalidOperationException("element is not a wall.");
                    var curve = (wall.Location as LocationCurve)?.Curve;
                    if (curve == null) throw new InvalidOperationException("the wall has no location line.");
                    return curve.Reference ?? throw new InvalidOperationException("Revit exposes no centerline reference for this wall.");
            }
        }

        /// <summary>A line between the two elements' bounding box centres, moved 1 ft sideways in the view plane.</summary>
        private static Line InferLine(View view, Element first, Element second)
        {
            var b1 = first.get_BoundingBox(view) ?? first.get_BoundingBox(null);
            var b2 = second.get_BoundingBox(view) ?? second.get_BoundingBox(null);
            if (b1 == null || b2 == null) return null;
            var c1 = (b1.Min + b1.Max) * 0.5;
            var c2 = (b2.Min + b2.Max) * 0.5;
            var dir = c2 - c1;
            if (dir.GetLength() <= 0.001) return null;
            dir = dir.Normalize();
            var side = Math.Abs(view.ViewDirection.DotProduct(XYZ.BasisZ)) < 0.9
                ? dir.CrossProduct(view.ViewDirection)
                : new XYZ(-dir.Y, dir.X, 0);
            if (side.GetLength() <= 1e-9) return null;
            side = side.Normalize();
            return Line.CreateBound(c1 + side, c2 + side);
        }

        private static bool TryPointMm(JToken token, string what, out XYZ point, out CommandResult failure)
        {
            point = null;
            failure = null;
            var o = token as JObject;
            if (o != null && AnnotationHelpers.IsNumber(o["x"]) && AnnotationHelpers.IsNumber(o["y"]) && AnnotationHelpers.IsNumber(o["z"]))
            {
                point = new XYZ(Mm.ToFeet(o.Value<double>("x")), Mm.ToFeet(o.Value<double>("y")), Mm.ToFeet(o.Value<double>("z")));
                return true;
            }
            failure = CommandResult.Fail("INVALID_PARAMS: " + what + " must be {\"x\": mm, \"y\": mm, \"z\": mm}.");
            return false;
        }
    }
}
