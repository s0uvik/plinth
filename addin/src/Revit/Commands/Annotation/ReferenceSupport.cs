// New in Plinth (Step 9 T10b): dimensionable references shared by create_dimensions and auto_dimension_section.
//   - PlanarFaces: every planar face of an element with a reference Revit can dimension to. Faces of an element's own
//     solids carry their references directly; faces of a family instance's symbol geometry (an instance whose geometry
//     is not modified by joins or cuts) are turned into instance references through the stable representation
//     "<instance UniqueId>:0:INSTANCE:<symbol face reference>", the documented route for dimensioning instance faces.
//     Nested family geometry is not walked.
//   - WallFace: a wall's exterior/interior (or left/right of start->end) side face, from HostObjectUtils.GetSideFaces,
//     else the wall's own geometry faces facing that side (outermost);
//   - DirectionalFace: an element's planar faces whose normal is +/- a direction, outermost first (slab and footing
//     edges, column faces);
//   - InstanceReference: FamilyInstance.GetReferences for a FamilyInstanceReferenceType, with the types the instance
//     does have when the wanted one is missing.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Plinth.Revit.Core.Drawing;

namespace Plinth.Revit.Commands.Annotation
{
    // Plinth.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    /// <summary>A planar face with a reference, in model coordinates.</summary>
    internal sealed class FaceRef
    {
        public Reference Reference;
        public XYZ Normal;
        public XYZ Origin;
        public double Area;

        /// <summary>How the reference was obtained, for reports.</summary>
        public string Via;
    }

    internal static class ReferenceSupport
    {
        private const double ParallelTolerance = 1e-4;

        /// <summary>The stable representation of a reference, or null when Revit cannot give one.</summary>
        public static string Stable(Document doc, Reference reference)
        {
            if (reference == null) return null;
            try { return reference.ConvertToStableRepresentation(doc); }
            catch (Exception) { return null; }
        }

        /// <summary>Planar faces with references: view-specific geometry first (when a view is given), else fine geometry.</summary>
        public static List<FaceRef> PlanarFaces(Document doc, Element element, View view)
        {
            var result = new List<FaceRef>();
            if (view != null)
            {
                try { Walk(doc, element, element.get_Geometry(new Options { ComputeReferences = true, View = view }), result); }
                catch (Exception) { result.Clear(); }
            }
            if (result.Count > 0) return result;
            try { Walk(doc, element, element.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine }), result); }
            catch (Exception) { result.Clear(); }
            return result;
        }

        private static void Walk(Document doc, Element element, GeometryElement geometry, List<FaceRef> into)
        {
            if (geometry == null) return;
            foreach (var obj in geometry)
            {
                if (obj is Solid solid)
                {
                    foreach (Face face in solid.Faces)
                    {
                        if (!(face is PlanarFace pf) || pf.Reference == null) continue;
                        into.Add(new FaceRef { Reference = pf.Reference, Normal = pf.FaceNormal, Origin = pf.Origin, Area = pf.Area, Via = "element geometry face" });
                    }
                }
                else if (obj is GeometryInstance instance && element is FamilyInstance fi)
                {
                    var t = instance.Transform;
                    GeometryElement symbol;
                    try { symbol = instance.GetSymbolGeometry(); }
                    catch (Exception) { continue; }
                    if (symbol == null) continue;
                    foreach (var solidInSymbol in symbol.OfType<Solid>())
                    {
                        foreach (Face face in solidInSymbol.Faces)
                        {
                            if (!(face is PlanarFace pf) || pf.Reference == null) continue;
                            var symbolStable = Stable(doc, pf.Reference);
                            if (symbolStable == null) continue;
                            Reference reference;
                            try { reference = Reference.ParseFromStableRepresentation(doc, fi.UniqueId + ":0:INSTANCE:" + symbolStable); }
                            catch (Exception) { reference = null; }
                            if (reference == null) continue;
                            into.Add(new FaceRef
                            {
                                Reference = reference,
                                Normal = t.OfVector(pf.FaceNormal).Normalize(),
                                Origin = t.OfPoint(pf.Origin),
                                Area = pf.Area,
                                Via = "family symbol face as an instance reference"
                            });
                        }
                    }
                }
            }
        }

        /// <summary>
        /// The element's planar faces whose normal is +direction (positiveEnd) or -direction, outermost first, one per
        /// plane (the largest face at each position). Direction is a unit vector.
        /// </summary>
        public static List<FaceRef> DirectionalFaces(Document doc, Element element, View view, XYZ direction, bool positiveEnd)
        {
            var faces = PlanarFaces(doc, element, view).OrderByDescending(f => f.Area).ToList();
            var candidates = faces.Select((f, i) => new FaceCandidate(DrawingSupport.ToMm(f.Origin.DotProduct(direction)),
                f.Normal.DotProduct(direction), i));
            return FacePick.Outermost(candidates, positiveEnd, 0.5, ParallelTolerance).Select(c => faces[c.Key]).ToList();
        }

        /// <summary>Distinct directions (rounded unit normals) the element's planar faces face, for error messages.</summary>
        public static List<string> FaceDirections(Document doc, Element element, View view) =>
            PlanarFaces(doc, element, view)
                .Select(f => "(" + Math.Round(f.Normal.X, 3) + ", " + Math.Round(f.Normal.Y, 3) + ", " + Math.Round(f.Normal.Z, 3) + ")")
                .Distinct().Take(30).ToList();

        /// <summary>
        /// A wall side face. face: exterior | interior | left | right (left/right of the wall's start->end direction seen
        /// from above; straight walls only). Null with an error when there is no flat face on that side.
        /// </summary>
        public static FaceRef WallFace(Document doc, Wall wall, string face, View view, out string error)
        {
            error = null;
            var curve = (wall.Location as LocationCurve)?.Curve;
            var name = (face ?? "").Trim().ToLowerInvariant();
            double dot = 0;
            if (name == "left" || name == "right")
            {
                if (!(curve is Line line))
                {
                    error = "left/right needs a straight wall; use exterior or interior.";
                    return null;
                }
                dot = wall.Orientation.DotProduct(XYZ.BasisZ.CrossProduct(line.Direction));
            }
            var side = WallSides.Resolve(name, dot);
            if (side == null)
            {
                error = WallSides.Names.Contains(name)
                    ? "the wall's exterior is not to its left or right (vertical or odd orientation); use exterior or interior."
                    : "face must be one of " + string.Join(", ", WallSides.Names) + ".";
                return null;
            }

            var candidates = new List<FaceRef>();
            try
            {
                foreach (var r in HostObjectUtils.GetSideFaces(wall, side == "exterior" ? ShellLayerType.Exterior : ShellLayerType.Interior))
                {
                    if (wall.GetGeometryObjectFromReference(r) is PlanarFace pf)
                        candidates.Add(new FaceRef { Reference = r, Normal = pf.FaceNormal, Origin = pf.Origin, Area = pf.Area, Via = "HostObjectUtils side face" });
                }
            }
            catch (Exception)
            {
                // Stacked or curtain walls have no side faces of their own; the geometry fallback below handles them.
            }
            if (candidates.Count > 0) return candidates.OrderByDescending(c => c.Area).First();

            if (!(curve is Line))
            {
                error = "the wall's " + side + " face is not flat; a linear dimension needs a flat face (dimension the centerline instead).";
                return null;
            }
            var outward = side == "exterior" ? wall.Orientation : wall.Orientation.Negate();
            var fromGeometry = DirectionalFaces(doc, wall, view, outward.Normalize(), true);
            if (fromGeometry.Count > 0)
            {
                fromGeometry[0].Via = "wall geometry face (outermost on the " + side + " side)";
                return fromGeometry[0];
            }
            error = "the wall has no flat " + side + " face with a reference.";
            return null;
        }

        /// <summary>
        /// FamilyInstance.GetReferences for a type name (Left, Right, CenterLeftRight, Front, Back, CenterFrontBack, Top,
        /// Bottom; case and underscores ignored). Null with an error naming the types the instance has when missing.
        /// </summary>
        public static Reference InstanceReference(FamilyInstance instance, string name, out string canonical, out string error)
        {
            error = null;
            if (!InstanceReferenceNames.TryNormalize(name, out canonical))
            {
                error = "reference must be one of " + string.Join(", ", InstanceReferenceNames.Names) + ".";
                return null;
            }
            var type = (FamilyInstanceReferenceType)Enum.Parse(typeof(FamilyInstanceReferenceType), canonical);
            var found = instance.GetReferences(type).FirstOrDefault();
            if (found != null) return found;
            var available = InstanceReferenceNames.Names
                .Where(n => instance.GetReferences((FamilyInstanceReferenceType)Enum.Parse(typeof(FamilyInstanceReferenceType), n)).Count > 0)
                .ToList();
            error = "the family has no " + canonical + " reference; it has "
                + (available.Count == 0 ? "none of the named references" : string.Join(", ", available)) + ".";
            return null;
        }
    }
}
