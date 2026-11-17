// New in Plinth (Step 10, F5): work-plane-based (face-based) families - sockets, switches, wall-mounted AC units, ceiling
// lights - are placed on a face of their host: a straight wall's exterior or interior side face, a ceiling's bottom
// face, or a floor's or roof's top face. This finds the face reference, the point on it and the in-face direction that
// NewFamilyInstance(Reference, XYZ, XYZ, FamilySymbol) needs.

using System;
using Autodesk.Revit.DB;

namespace Plinth.Revit.Commands.Create
{
    internal static class FacePlacement
    {
        public sealed class Target
        {
            public Reference Face;
            public XYZ Location;
            public XYZ ReferenceDirection;
            public XYZ Normal;

            /// <summary>exterior, interior, bottom or top.</summary>
            public string Side;
        }

        /// <summary>
        /// The wall's side face on the requested side (side "exterior"/"interior", or the side facing toward, a plan point).
        /// The point is moved onto the face plane; the reference direction runs along the wall, so the family stays upright.
        /// </summary>
        public static bool TryWallFace(Wall wall, XYZ point, string side, XYZ toward, out Target target, out string error)
        {
            target = null;
            error = null;
            if (!TryResolveSide(wall, point, side, toward, out side, out error)) return false;

            var references = HostObjectUtils.GetSideFaces(wall, side == "exterior" ? ShellLayerType.Exterior : ShellLayerType.Interior);
            PlanarFace fallback = null;
            foreach (var reference in references)
            {
                var face = wall.GetGeometryObjectFromReference(reference) as PlanarFace;
                if (face == null || Math.Abs(face.FaceNormal.Z) > 1e-6) continue;
                var onPlane = point - face.FaceNormal * (point - face.Origin).DotProduct(face.FaceNormal);
                if (face.Project(onPlane) == null)
                {
                    if (fallback == null) fallback = face;
                    continue;
                }
                target = new Target
                {
                    Face = reference,
                    Location = onPlane,
                    Normal = face.FaceNormal,
                    ReferenceDirection = XYZ.BasisZ.CrossProduct(face.FaceNormal).Normalize(),
                    Side = side
                };
                return true;
            }
            error = fallback == null
                ? "NO_WALL_FACE: the wall has no flat " + side + " side face to host on."
                : "POINT_NOT_ON_FACE: the point is not on the wall's " + side + " face (it falls in an opening, or beyond the wall's ends or height).";
            return false;
        }

        /// <summary>
        /// "exterior" or "interior": the side asked for, or the side of the wall the toward point (a plan point) lies on.
        /// </summary>
        public static bool TryResolveSide(Wall wall, XYZ point, string side, XYZ toward, out string resolved, out string error)
        {
            resolved = null;
            error = null;
            var line = (wall.Location as LocationCurve)?.Curve as Line;
            if (line == null)
            {
                error = "HOST_NOT_STRAIGHT_WALL: placing on a side of a wall needs a straight wall.";
                return false;
            }
            if (toward != null)
            {
                var foot = line.Project(new XYZ(point.X, point.Y, line.GetEndPoint(0).Z))?.XYZPoint ?? line.GetEndPoint(0);
                var offset = new XYZ(toward.X - foot.X, toward.Y - foot.Y, 0);
                if (offset.GetLength() < 1e-6)
                {
                    error = "INVALID_PARAMS: the toward point lies on the wall's location line; give a point inside the room or outside.";
                    return false;
                }
                side = offset.DotProduct(wall.Orientation) > 0 ? "exterior" : "interior";
            }
            if (side != "exterior" && side != "interior")
            {
                error = "INVALID_PARAMS: say which side of the wall to place on: wallSide exterior or interior, or towardX/towardY (a point on that side).";
                return false;
            }
            resolved = side;
            return true;
        }

        /// <summary>A ceiling's bottom face, or a floor's or roof's top face, directly above or below the point.</summary>
        public static bool TryHorizontalFace(HostObject host, XYZ point, double rotationRad, out Target target, out string error)
        {
            target = null;
            error = null;
            var bottom = host is Ceiling;
            var references = bottom ? HostObjectUtils.GetBottomFaces(host) : HostObjectUtils.GetTopFaces(host);
            foreach (var reference in references)
            {
                var face = host.GetGeometryObjectFromReference(reference) as PlanarFace;
                if (face == null || Math.Abs(Math.Abs(face.FaceNormal.Z) - 1) > 1e-6) continue;
                var projected = face.Project(point)?.XYZPoint;
                if (projected == null || Math.Abs(projected.X - point.X) > 1e-6 || Math.Abs(projected.Y - point.Y) > 1e-6) continue;
                target = new Target
                {
                    Face = reference,
                    Location = projected,
                    Normal = face.FaceNormal,
                    ReferenceDirection = new XYZ(Math.Cos(rotationRad), Math.Sin(rotationRad), 0),
                    Side = bottom ? "bottom" : "top"
                };
                return true;
            }
            error = "POINT_NOT_ON_FACE: no flat " + (bottom ? "bottom" : "top") + " face of host " + host.Id + " lies over the point.";
            return false;
        }
    }
}
