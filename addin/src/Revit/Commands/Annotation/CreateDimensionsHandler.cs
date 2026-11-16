// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Annotations/CreateDimensionsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: every reference must resolve or nothing is created (rvt-mcp dimensioned the ones that
// resolved, and returned success with created=false when fewer than two did); the nested references and line
// objects are checked here because the schema checker stops at the top level; a bad dimension_type_id lists the
// valid linear dimension types; a line given with coincident ends is an error instead of being replaced by the
// inferred one; mm via Mm.
// Step 9 T10b (drawing set for imported projects): more reference kinds, each entry saying which reference it means:
// wall_face (a wall's exterior/interior side face, or the face left/right of its start->end direction:
// HostObjectUtils.GetSideFaces, else the wall's geometry faces), family_instance (FamilyInstance.GetReferences for
// Left/Right/CenterLeftRight/Front/Back/CenterFrontBack/Top/Bottom: doors, windows, columns), level
// (Level.GetPlaneReference), grid (alias of grid_curve) and element_face (planar faces of floors, foundation slabs,
// footings or columns whose normal is +/- a direction, outermost first; see ReferenceSupport). mode may be left out
// when the entry says which reference (face -> wall_face, reference -> family_instance, direction -> element_face) or
// the element says it (wall -> wall_centerline, grid, level). Before anything is created every face must cross the
// line at a right angle, and no reference may repeat or sit at another's position (a zero-length segment); after
// creation a segment under 0.5 mm rolls the dimension back.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using B2M.Revit.Core.Drawing;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Annotation
{
    // B2M.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class CreateDimensionsHandler : IRevitCommand
    {
        private static readonly string[] Modes =
            { "wall_centerline", "grid_curve", "explicit_reference_stable", "wall_face", "family_instance", "level", "grid", "element_face" };
        private static readonly string[] ItemFields = { "mode", "element_id", "stable_reference", "face", "reference", "direction", "end", "index" };

        /// <summary>Two references closer than this along the line would make a zero-length segment.</summary>
        private const double ZeroSegmentMm = 0.5;

        public string Name => "create_dimensions";
        public string Description =>
            "Create one dimension string in a view (view_id, default the active view) through two or more references, each "
            + "{mode, element_id, stable_reference?}: mode wall_centerline (a wall), grid_curve or grid (a grid), level (a level), "
            + "explicit_reference_stable (a stable reference string), wall_face (a wall side: face exterior|interior|left|right, "
            + "left/right of the wall's start->end), family_instance (a door/window/column reference: reference Left|Right|"
            + "CenterLeftRight|Front|Back|CenterFrontBack|Top|Bottom) or element_face (a slab, footing or column face: direction "
            + "{x,y,z} or x|y|z|-x|-y|-z, default the dimension line's direction; end positive (default, the outermost face facing "
            + "+direction) or negative; index 0 = outermost). mode can be left out when face/reference/direction or the element "
            + "says it. line {start:{x,y,z}, end:{x,y,z}} in mm places the dimension line; without it the line runs between the "
            + "first two elements' centres, 304.8 mm to one side. No reference may repeat or coincide with another. "
            + "Optional dimension_type_id.";
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

            var resolvedRefs = new List<ResolvedRef>();
            var elements = new List<Element>();
            var resolved = new JArray();
            var failed = new JArray();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i] as JObject;
                var idToken = item?["element_id"];
                var mode = item?.Value<string>("mode");
                var stray = item == null ? new List<string>() : item.Properties().Select(x => x.Name).Where(n => !ItemFields.Contains(n)).ToList();
                if (item == null || (mode != null && !Modes.Contains(mode)) || idToken == null || idToken.Type != JTokenType.Integer || stray.Count > 0)
                    return CommandResult.Fail("INVALID_PARAMS: references[" + i + "] must be {\"mode\"?: one of "
                        + string.Join(", ", Modes) + ", \"element_id\": integer, \"stable_reference\"?: string, \"face\"?: "
                        + string.Join("|", WallSides.Names) + ", \"reference\"?: " + string.Join("|", InstanceReferenceNames.Names)
                        + ", \"direction\"?: {x,y,z} or x|y|z|-x|-y|-z, \"end\"?: positive|negative, \"index\"?: integer}"
                        + (stray.Count > 0 ? "; unknown field(s) " + string.Join(", ", stray) : "") + ".");
                var elementId = idToken.Value<long>();
                try
                {
                    var element = doc.GetElement(RevitCompat.ToElementId(elementId));
                    if (element == null) throw new InvalidOperationException("element not found.");
                    mode = mode ?? InferMode(element, item);
                    var reference = Resolve(doc, view, element, mode, item, line);
                    resolvedRefs.Add(reference);
                    elements.Add(element);
                    resolved.Add(new JObject { ["mode"] = mode, ["element_id"] = elementId, ["reference"] = reference.Source });
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

            // Every face must cross the dimension line at a right angle; no reference twice and no two at one position.
            var lineStart = line.GetEndPoint(0);
            var lineDir = (line.GetEndPoint(1) - lineStart).Normalize();
            var planned = new List<PlannedReference>();
            var notAcross = new JArray();
            for (var i = 0; i < resolvedRefs.Count; i++)
            {
                var r = resolvedRefs[i];
                var position = r.PositionMm(lineStart, lineDir, out var across);
                if (!across)
                    notAcross.Add(new JObject { ["index"] = i, ["reference"] = r.Source, ["error"] = "it is not perpendicular to the dimension line" });
                if (position.HasValue) ((JObject)resolved[i])["position_mm"] = Math.Round(position.Value, 1);
                planned.Add(new PlannedReference(i, ReferenceSupport.Stable(doc, r.Reference), position, r.Source));
            }
            if (notAcross.Count > 0)
                return CommandResult.Fail("REFERENCE_NOT_ACROSS: " + notAcross.Count + " reference(s) do not cross the dimension line at a right "
                    + "angle; see failed_references (pick faces whose normal runs along the line, or move the line). Nothing was created.",
                    new JObject { ["resolved_references"] = resolved, ["failed_references"] = notAcross });
            var conflicts = ReferenceChecks.Conflicts(planned, ZeroSegmentMm);
            if (conflicts.Count > 0)
                return CommandResult.Fail("DUPLICATE_REFERENCES: " + conflicts.Count + " pair(s) of references would repeat a reference or make a "
                    + "zero-length segment; drop one of each pair. Nothing was created.", new JObject
                    {
                        ["resolved_references"] = resolved,
                        ["conflicts"] = new JArray(conflicts.Select(c => new JObject { ["first"] = c.First, ["second"] = c.Second, ["reason"] = c.Reason }))
                    });

            var references = new ReferenceArray();
            foreach (var r in resolvedRefs) references.Append(r.Reference);

            Dimension dimension;
            using (var tx = new Transaction(doc, "B2M: Create Dimension"))
            {
                tx.Start();
                try
                {
                    dimension = doc.Create.NewDimension(view, line, references);
                    if (dimension == null) throw new InvalidOperationException("Revit returned no dimension.");
                    if (dimType != null) dimension.ChangeTypeId(dimType.Id);
                    var zero = ZeroSegments(dimension);
                    if (zero != null) throw new InvalidOperationException(zero);
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
                ["values_mm"] = DrawingSupport.Values(dimension),
                ["line_mm"] = new JObject
                {
                    ["start"] = AnnotationHelpers.PointMm(line.GetEndPoint(0)),
                    ["end"] = AnnotationHelpers.PointMm(line.GetEndPoint(1))
                },
                ["resolved_references"] = resolved
            });
        }

        /// <summary>A resolved reference and what is known of where it is (to check it against the dimension line).</summary>
        private sealed class ResolvedRef
        {
            public Reference Reference;
            public string Source;

            /// <summary>A flat face: a point on it and its normal. It must cross the line at a right angle.</summary>
            public XYZ PlaneOrigin;
            public XYZ PlaneNormal;

            /// <summary>A straight grid or wall centreline (vertical plane through it).</summary>
            public Line Axis;

            /// <summary>A level's height (horizontal plane).</summary>
            public double? Z;

            /// <summary>
            /// Position (mm) along the line from its start when known. across is false only when the reference is a
            /// face or level plane that does not cross the line at a right angle; grids, centrelines and family
            /// reference planes at an angle are left to Revit (as before).
            /// </summary>
            public double? PositionMm(XYZ start, XYZ dir, out bool across)
            {
                across = true;
                if (PlaneNormal != null)
                {
                    across = Math.Abs(PlaneNormal.DotProduct(dir)) > 0.9999;
                    return across ? DrawingSupport.ToMm((PlaneOrigin - start).DotProduct(dir)) : (double?)null;
                }
                if (Z.HasValue)
                {
                    across = Math.Abs(dir.Z) > 0.9999;
                    return across ? DrawingSupport.ToMm((Z.Value - start.Z) * dir.Z) : (double?)null;
                }
                if (Axis != null && Math.Abs(Axis.Direction.DotProduct(dir)) < 1e-4)
                {
                    var o = Axis.Origin;
                    var flat = Math.Abs(dir.Z) < 1e-9 ? new XYZ(o.X, o.Y, start.Z) : o;
                    return DrawingSupport.ToMm((flat - start).DotProduct(dir));
                }
                return null;
            }
        }

        /// <summary>The mode an entry without one means: from its fields, else from the element.</summary>
        private static string InferMode(Element element, JObject item)
        {
            if (item["stable_reference"] != null) return "explicit_reference_stable";
            if (item["face"] != null) return "wall_face";
            if (item["reference"] != null) return "family_instance";
            if (item["direction"] != null || item["end"] != null || item["index"] != null) return "element_face";
            if (element is Wall) return "wall_centerline";
            if (element is Grid) return "grid_curve";
            if (element is Level) return "level";
            throw new InvalidOperationException("say which reference: mode, or face (walls), reference (family instances) or direction "
                + "(faces of slabs, footings, columns).");
        }

        private static ResolvedRef Resolve(Document doc, View view, Element element, string mode, JObject item, Line line)
        {
            switch (mode)
            {
                case "explicit_reference_stable":
                {
                    var stable = item.Value<string>("stable_reference");
                    if (string.IsNullOrEmpty(stable)) throw new InvalidOperationException("stable_reference is required for explicit_reference_stable.");
                    var reference = Reference.ParseFromStableRepresentation(doc, stable)
                        ?? throw new InvalidOperationException("stable_reference did not parse.");
                    return new ResolvedRef { Reference = reference, Source = "stable reference" };
                }
                case "grid_curve":
                case "grid":
                {
                    if (!(element is Grid grid)) throw new InvalidOperationException("element is not a grid.");
                    return new ResolvedRef { Reference = new Reference(grid), Source = "grid " + grid.Name, Axis = grid.Curve as Line };
                }
                case "level":
                {
                    if (!(element is Level level)) throw new InvalidOperationException("element is not a level.");
                    var reference = level.GetPlaneReference() ?? throw new InvalidOperationException("Revit gives no plane reference for this level.");
                    return new ResolvedRef { Reference = reference, Source = "level " + level.Name, Z = level.ProjectElevation };
                }
                case "wall_face":
                {
                    if (!(element is Wall wall)) throw new InvalidOperationException("element is not a wall.");
                    var face = item.Value<string>("face");
                    if (string.IsNullOrEmpty(face)) throw new InvalidOperationException("wall_face needs face: " + string.Join(", ", WallSides.Names) + ".");
                    var found = ReferenceSupport.WallFace(doc, wall, face, view, out var error) ?? throw new InvalidOperationException(error);
                    return new ResolvedRef
                    {
                        Reference = found.Reference, Source = "wall " + face.Trim().ToLowerInvariant() + " face (" + found.Via + ")",
                        PlaneOrigin = found.Origin, PlaneNormal = found.Normal
                    };
                }
                case "family_instance":
                {
                    if (!(element is FamilyInstance instance)) throw new InvalidOperationException("element is not a family instance.");
                    var reference = ReferenceSupport.InstanceReference(instance, item.Value<string>("reference"), out var canonical, out var error)
                        ?? throw new InvalidOperationException(error);
                    return new ResolvedRef { Reference = reference, Source = "family reference " + canonical };
                }
                case "element_face":
                {
                    if (!TryDirection(item["direction"], line, out var direction, out var error)) throw new InvalidOperationException(error);
                    var end = (item.Value<string>("end") ?? "positive").Trim().ToLowerInvariant();
                    if (end != "positive" && end != "negative") throw new InvalidOperationException("end must be positive or negative.");
                    var indexToken = item["index"];
                    if (indexToken != null && (indexToken.Type != JTokenType.Integer || indexToken.Value<int>() < 0))
                        throw new InvalidOperationException("index must be an integer, 0 (outermost) or more.");
                    var index = indexToken?.Value<int>() ?? 0;
                    var faces = ReferenceSupport.DirectionalFaces(doc, element, view, direction, end == "positive");
                    if (faces.Count == 0)
                        throw new InvalidOperationException("no flat face of the element faces " + (end == "positive" ? "+" : "-") + "direction ("
                            + Math.Round(direction.X, 3) + ", " + Math.Round(direction.Y, 3) + ", " + Math.Round(direction.Z, 3) + "); its faces face "
                            + string.Join(", ", ReferenceSupport.FaceDirections(doc, element, view)) + ".");
                    if (index >= faces.Count)
                        throw new InvalidOperationException("index " + index + " is out of range; the element has " + faces.Count
                            + " face plane(s) facing that way (index 0 to " + (faces.Count - 1) + ").");
                    var face = faces[index];
                    return new ResolvedRef
                    {
                        Reference = face.Reference, Source = "face " + index + " from the " + end + " end (" + face.Via + ")",
                        PlaneOrigin = face.Origin, PlaneNormal = face.Normal
                    };
                }
                default:
                {
                    if (!(element is Wall wall)) throw new InvalidOperationException("element is not a wall.");
                    var curve = (wall.Location as LocationCurve)?.Curve;
                    if (curve == null) throw new InvalidOperationException("the wall has no location line.");
                    var reference = curve.Reference ?? throw new InvalidOperationException("Revit exposes no centerline reference for this wall.");
                    return new ResolvedRef { Reference = reference, Source = "wall centerline", Axis = curve as Line };
                }
            }
        }

        /// <summary>direction as {x,y,z} or an axis name (x, -y, ...); without it, the dimension line's direction.</summary>
        private static bool TryDirection(JToken token, Line line, out XYZ direction, out string error)
        {
            direction = null;
            error = null;
            if (token == null || token.Type == JTokenType.Null)
            {
                if (line == null)
                {
                    error = "element_face needs direction when no line is given.";
                    return false;
                }
                direction = line.Direction.Normalize();
                return true;
            }
            if (token.Type == JTokenType.String)
            {
                switch (token.Value<string>().Trim().ToLowerInvariant())
                {
                    case "x": case "+x": direction = XYZ.BasisX; return true;
                    case "-x": direction = XYZ.BasisX.Negate(); return true;
                    case "y": case "+y": direction = XYZ.BasisY; return true;
                    case "-y": direction = XYZ.BasisY.Negate(); return true;
                    case "z": case "+z": direction = XYZ.BasisZ; return true;
                    case "-z": direction = XYZ.BasisZ.Negate(); return true;
                }
            }
            else if (token is JObject o && AnnotationHelpers.IsNumber(o["x"]) && AnnotationHelpers.IsNumber(o["y"]) && AnnotationHelpers.IsNumber(o["z"]))
            {
                var v = new XYZ(o.Value<double>("x"), o.Value<double>("y"), o.Value<double>("z"));
                if (v.GetLength() > 1e-9)
                {
                    direction = v.Normalize();
                    return true;
                }
            }
            error = "direction must be {\"x\", \"y\", \"z\"} (not all 0) or one of x, y, z, -x, -y, -z.";
            return false;
        }

        /// <summary>Null when every segment has a length; else which segment is zero (the dimension is then rolled back).</summary>
        private static string ZeroSegments(Dimension dimension)
        {
            if (dimension.NumberOfSegments > 1)
            {
                var i = 0;
                foreach (DimensionSegment segment in dimension.Segments)
                {
                    if (segment.Value.HasValue && DrawingSupport.ToMm(segment.Value.Value) < ZeroSegmentMm)
                        return "segment " + i + " (between references " + i + " and " + (i + 1) + ") has zero length.";
                    i++;
                }
                return null;
            }
            return dimension.Value.HasValue && DrawingSupport.ToMm(dimension.Value.Value) < ZeroSegmentMm
                ? "the dimension has zero length." : null;
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
