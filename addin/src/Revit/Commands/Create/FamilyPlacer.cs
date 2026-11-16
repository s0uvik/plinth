// New in B2M (Step 10, F5): one placement routine for create_point_based_element, create_lighting_fixture and
// create_air_terminal. It places a family type in the way PlacementRules chooses: free at a level point (rotation,
// height); hosted by a wall at a distance along it, at a height, on a chosen side; face-based on a wall's side face,
// on a ceiling's bottom face or a floor's top face; or, for a work-plane-based family with no host (a light or fan where
// there is no ceiling), on the level's plane facing up or on a new reference plane at the height facing down.
// One transaction; the position (and host) Revit gives is checked to 1 mm before commit, otherwise everything rolls
// back. created_ids lists every element added (the instance and any reference plane) so a run can remove them.

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using B2M.Revit.Bridge;
using B2M.Revit.Core.Families;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Create
{
    internal static class FamilyPlacer
    {
        private static readonly double Tolerance = Mm.ToFeet(1.0);

        /// <summary>What to place and where; lengths in mm. Read from either naming style by Read.</summary>
        public sealed class Request
        {
            public long? HostId;
            public double? HostOffset;
            public double? X, Y, Z, Offset;
            public double? RotationDeg;
            public string WallSide;
            public double? TowardX, TowardY;
            public bool FaceDown;
            public double? SillHeight;
            public bool FlipFacing, FlipHand;
        }

        public sealed class Placed
        {
            public FamilyInstance Instance;
            public PlacementRules.Mode Mode;
            public Element Host;
            public string Side;
            public XYZ Location;
            public List<long> CreatedIds = new List<long>();
            public string Warning;
        }

        /// <summary>Schema properties shared by the placing commands (camelCase for create_point_based_element, else snake_case).</summary>
        public static string SchemaProperties(bool snake) =>
            @"""x"":{""type"":""number""},""y"":{""type"":""number""},""z"":{""type"":""number""},
            """ + N("offset", snake) + @""":{""type"":""number""},
            ""host_id"":{""type"":""integer""},
            """ + N("hostOffset", snake) + @""":{""type"":""number""},
            """ + N("rotation", snake) + @""":{""type"":""number""},
            """ + N("wallSide", snake) + @""":{""type"":""string"",""enum"":[""exterior"",""interior""]},
            """ + N("towardX", snake) + @""":{""type"":""number""},
            """ + N("towardY", snake) + @""":{""type"":""number""},
            """ + N("faceDown", snake) + @""":{""type"":""boolean""},
            """ + N("flipFacing", snake) + @""":{""type"":""boolean""},
            """ + N("flipHand", snake) + @""":{""type"":""boolean""}";

        public const string OptionsText =
            "Position: x, y (mm), or hostOffset (mm along a straight wall from its start) with host_id. Height: offset (mm above the "
            + "level) or z (absolute mm); on a wall it is the height of the point on the wall. rotation (degrees, about vertical) for "
            + "level-based families and on a ceiling/floor face. Wall-hosted and face-based families on a wall: host_id of the wall "
            + "and the side, wallSide exterior/interior or towardX/towardY (a plan point on that side, e.g. inside the room). "
            + "Face-based on a ceiling (bottom face), floor or roof (top face): host_id and x, y. Face-based without a host: on the "
            + "level's plane facing up, raised by offset; faceDown true places it on a new reference plane at the height facing "
            + "down (a light or fan where there is no ceiling; the plane is listed in created_ids).";

        private static string N(string camel, bool snake)
        {
            if (!snake) return camel;
            var chars = new System.Text.StringBuilder();
            foreach (var c in camel)
            {
                if (char.IsUpper(c)) chars.Append('_').Append(char.ToLowerInvariant(c));
                else chars.Append(c);
            }
            return chars.ToString();
        }

        public static bool TryRead(JObject p, bool snake, out Request request, out CommandResult failure)
        {
            request = new Request
            {
                HostId = p.Value<long?>("host_id"),
                HostOffset = p.Value<double?>(N("hostOffset", snake)),
                X = p.Value<double?>("x"),
                Y = p.Value<double?>("y"),
                Z = p.Value<double?>("z"),
                Offset = p.Value<double?>(N("offset", snake)),
                RotationDeg = p.Value<double?>(N("rotation", snake)),
                WallSide = p.Value<string>(N("wallSide", snake)),
                TowardX = p.Value<double?>(N("towardX", snake)),
                TowardY = p.Value<double?>(N("towardY", snake)),
                FaceDown = p.Value<bool?>(N("faceDown", snake)) ?? false,
                SillHeight = snake ? null : p.Value<double?>("sillHeight"),
                FlipFacing = p.Value<bool?>(N("flipFacing", snake)) ?? false,
                FlipHand = p.Value<bool?>(N("flipHand", snake)) ?? false
            };
            failure = null;
            var r = request;
            var hasXY = r.X.HasValue && r.Y.HasValue;
            if (r.X.HasValue != r.Y.HasValue) failure = CommandResult.Fail("INVALID_PARAMS: give x and y together.");
            else if (r.HostOffset.HasValue == hasXY) failure = CommandResult.Fail("INVALID_PARAMS: give either " + N("hostOffset", snake) + " (with host_id) or x and y, not both.");
            else if (r.HostOffset.HasValue && !r.HostId.HasValue) failure = CommandResult.Fail("INVALID_PARAMS: " + N("hostOffset", snake) + " needs host_id.");
            else if (r.Z.HasValue && r.Offset.HasValue) failure = CommandResult.Fail("INVALID_PARAMS: give z (absolute) or " + N("offset", snake) + " (above the level), not both.");
            else if (r.TowardX.HasValue != r.TowardY.HasValue) failure = CommandResult.Fail("INVALID_PARAMS: give " + N("towardX", snake) + " and " + N("towardY", snake) + " together.");
            else if (r.TowardX.HasValue && !string.IsNullOrEmpty(r.WallSide)) failure = CommandResult.Fail("INVALID_PARAMS: give " + N("wallSide", snake) + " or a toward point, not both.");
            else if (!string.IsNullOrEmpty(r.WallSide) && r.WallSide != "exterior" && r.WallSide != "interior")
                failure = CommandResult.Fail("INVALID_PARAMS: " + N("wallSide", snake) + " is exterior or interior.");
            else if (r.FlipFacing && (!string.IsNullOrEmpty(r.WallSide) || r.TowardX.HasValue))
                failure = CommandResult.Fail("INVALID_PARAMS: the wall side sets which way the family faces; omit " + N("flipFacing", snake) + ".");
            return failure == null;
        }

        /// <summary>Places the instance in one committed transaction, or returns the failure with nothing created.</summary>
        public static bool TryPlace(Document doc, FamilySymbol symbol, Level level, Request r, string what, out Placed placed, out CommandResult failure)
        {
            placed = null;
            failure = null;

            Element host = null;
            var hostKind = PlacementRules.HostKind.None;
            if (r.HostId.HasValue)
            {
                host = doc.GetElement(RevitCompat.ToElementId(r.HostId.Value));
                if (host == null)
                {
                    failure = CommandResult.Fail("ELEMENT_NOT_FOUND: host " + r.HostId.Value + " is not in this document.");
                    return false;
                }
                hostKind = host is Wall ? PlacementRules.HostKind.Wall
                    : host is Ceiling || host is Floor || host is RoofBase ? PlacementRules.HostKind.Horizontal
                    : PlacementRules.HostKind.Other;
            }

            var placementType = symbol.Family.FamilyPlacementType;
            var options = new PlacementRules.Options
            {
                HasHostOffset = r.HostOffset.HasValue,
                HasSide = !string.IsNullOrEmpty(r.WallSide) || r.TowardX.HasValue,
                FaceDown = r.FaceDown,
                HasRotation = r.RotationDeg.HasValue && Math.Abs(r.RotationDeg.Value) > 1e-9,
                HasHeight = r.Z.HasValue || r.Offset.HasValue
            };
            var chosen = PlacementRules.Choose(placementType.ToString(), hostKind, options, out var error, out var warning);
            if (chosen == null)
            {
                failure = CommandResult.Fail(error);
                return false;
            }
            var mode = chosen.Value;
            if (mode == PlacementRules.Mode.Level) host = null;
            var bic = symbol.Category?.BuiltInCategory;
            var isOpening = bic == BuiltInCategory.OST_Doors || bic == BuiltInCategory.OST_Windows;
            if (isOpening && mode == PlacementRules.Mode.Hosted)
            {
                failure = CommandResult.Fail("HOST_NOT_WALL: doors and windows need a wall as host.");
                return false;
            }
            if (r.SillHeight.HasValue && options.HasHeight)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: give sillHeight or a height (z, offset), not both.");
                return false;
            }

            // The point: along the wall's location line, or x, y; at the height asked (default the level).
            var wall = host as Wall;
            var height = r.Z.HasValue ? Mm.ToFeet(r.Z.Value) : level.ProjectElevation + Mm.ToFeet(r.Offset ?? 0);
            XYZ point;
            if (r.HostOffset.HasValue)
            {
                var line = (wall?.Location as LocationCurve)?.Curve as Line;
                if (line == null)
                {
                    failure = CommandResult.Fail("HOST_NOT_STRAIGHT_WALL: hostOffset needs a straight wall as host.");
                    return false;
                }
                var along = Mm.ToFeet(r.HostOffset.Value);
                if (along < 0 || along > line.Length)
                {
                    failure = CommandResult.Fail("OFFSET_OUT_OF_RANGE: the host offset must be between 0 and the wall length (" + Mm.FromFeet(line.Length) + " mm).");
                    return false;
                }
                var at = line.GetEndPoint(0) + line.Direction * along;
                point = new XYZ(at.X, at.Y, height);
            }
            else
            {
                point = new XYZ(Mm.ToFeet(r.X.Value), Mm.ToFeet(r.Y.Value), height);
            }
            var toward = r.TowardX.HasValue ? new XYZ(Mm.ToFeet(r.TowardX.Value), Mm.ToFeet(r.TowardY.Value), 0) : null;
            var rotation = (r.RotationDeg ?? 0) * Math.PI / 180.0;

            string side = null;
            FacePlacement.Target face = null;
            if (mode == PlacementRules.Mode.WallHosted && options.HasSide
                && !FacePlacement.TryResolveSide(wall, point, r.WallSide, toward, out side, out error))
            {
                failure = CommandResult.Fail(error);
                return false;
            }
            if (mode == PlacementRules.Mode.WallFace && !FacePlacement.TryWallFace(wall, point, r.WallSide, toward, out face, out error)
                || mode == PlacementRules.Mode.HorizontalFace && !FacePlacement.TryHorizontalFace((HostObject)host, point, rotation, out face, out error))
            {
                failure = CommandResult.Fail(error);
                return false;
            }

            using (var tx = new Transaction(doc, "B2M: Place " + what))
            {
                tx.Start();
                try
                {
                    if (!symbol.IsActive)
                    {
                        symbol.Activate();
                        doc.Regenerate();
                    }
                    placed = new Placed { Mode = mode, Host = host, Warning = warning };
                    var expected = point;
                    FamilyInstance instance;
                    switch (mode)
                    {
                        case PlacementRules.Mode.Level:
                            instance = doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural);
                            break;
                        case PlacementRules.Mode.WallHosted:
                        case PlacementRules.Mode.Hosted:
                            instance = doc.Create.NewFamilyInstance(point, symbol, host, level, StructuralType.NonStructural);
                            break;
                        case PlacementRules.Mode.WallFace:
                        case PlacementRules.Mode.HorizontalFace:
                            instance = doc.Create.NewFamilyInstance(face.Face, face.Location, face.ReferenceDirection, symbol);
                            expected = face.Location;
                            side = face.Side;
                            break;
                        case PlacementRules.Mode.LevelPlane:
                        {
                            var onLevel = new XYZ(point.X, point.Y, level.ProjectElevation);
                            var reference = level.GetPlaneReference() ?? throw new InvalidOperationException("Revit gives no plane reference for this level.");
                            instance = doc.Create.NewFamilyInstance(reference, onLevel, Direction(rotation), symbol);
                            if (instance != null && Math.Abs(point.Z - onLevel.Z) > 1e-9) SetOffset(instance, point.Z - onLevel.Z);
                            break;
                        }
                        default: // FaceDownPlane
                        {
                            var plane = DownwardPlane(doc, point);
                            placed.CreatedIds.Add(RevitCompat.GetId(plane.Id));
                            instance = doc.Create.NewFamilyInstance(plane.GetReference(), point, Direction(rotation), symbol);
                            side = "down";
                            break;
                        }
                    }
                    if (instance == null) throw new InvalidOperationException("Revit returned no instance.");
                    placed.Instance = instance;
                    placed.CreatedIds.Insert(0, RevitCompat.GetId(instance.Id));

                    if (mode == PlacementRules.Mode.Level && options.HasRotation)
                        ElementTransformUtils.RotateElement(doc, instance.Id, Line.CreateBound(point, point + XYZ.BasisZ), rotation);
                    if (r.SillHeight.HasValue)
                    {
                        var sillParam = instance.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                        if (sillParam == null || sillParam.IsReadOnly) throw new InvalidOperationException("this family has no settable sill height.");
                        sillParam.Set(Mm.ToFeet(r.SillHeight.Value));
                    }
                    if (r.FlipFacing && instance.CanFlipFacing) instance.flipFacing();
                    if (r.FlipHand && instance.CanFlipHand) instance.flipHand();
                    doc.Regenerate();
                    if (mode == PlacementRules.Mode.Level && !isOpening)
                    {
                        var fixedHeight = PutBackAtHeight(doc, instance, level, point);
                        if (fixedHeight != null) placed.Warning = placed.Warning == null ? fixedHeight : placed.Warning + " " + fixedHeight;
                    }

                    if (side != null && mode == PlacementRules.Mode.WallHosted)
                    {
                        var wanted = side == "exterior" ? wall.Orientation : wall.Orientation.Negate();
                        if (instance.FacingOrientation.DotProduct(wanted) < 0)
                        {
                            if (!instance.CanFlipFacing)
                                throw new InvalidOperationException("the family faces the wall's other side and cannot be flipped.");
                            instance.flipFacing();
                            doc.Regenerate();
                        }
                    }
                    placed.Side = side;

                    if (host != null && (instance.Host == null || instance.Host.Id != host.Id))
                        throw new InvalidOperationException("Revit did not assign the requested host.");
                    var actual = (instance.Location as LocationPoint)?.Point;
                    var miss = Miss(mode, actual, expected, wall, r.SillHeight.HasValue || !options.HasHeight && isOpening);
                    if (miss > Tolerance)
                        throw new InvalidOperationException("Revit did not place the instance within 1 mm of the requested point (requested "
                            + Describe(expected) + ", got " + (actual == null ? "none" : Describe(actual)) + ").");
                    placed.Location = actual;

                    if (tx.Commit() != TransactionStatus.Committed)
                    {
                        failure = CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures. Nothing was created.");
                        placed = null;
                        return false;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    placed = null;
                    failure = CommandResult.Fail("CREATE_FAILED: could not place the " + what + ": " + ex.Message + " Nothing was created.");
                    return false;
                }
            }
        }

        /// <summary>
        /// Distance from the requested point that counts. On a wall the family may sit on the location line or on a face,
        /// so only the distance along the wall (and the height, unless a sill height moves it) is compared.
        /// </summary>
        private static double Miss(PlacementRules.Mode mode, XYZ actual, XYZ expected, Wall wall, bool ignoreHeight)
        {
            if (actual == null) return double.MaxValue;
            var d = actual - expected;
            if (mode == PlacementRules.Mode.WallHosted)
            {
                var along = ((wall.Location as LocationCurve)?.Curve as Line)?.Direction;
                var horizontal = along == null ? new XYZ(d.X, d.Y, 0).GetLength() : Math.Abs(d.DotProduct(along));
                return ignoreHeight ? horizontal : Math.Max(horizontal, Math.Abs(d.Z));
            }
            if (mode == PlacementRules.Mode.Hosted) return new XYZ(d.X, d.Y, 0).GetLength();
            return d.GetLength();
        }

        private static XYZ Direction(double rotation) => new XYZ(Math.Cos(rotation), Math.Sin(rotation), 0);

        /// <summary>A horizontal reference plane through the point whose normal points down.</summary>
        private static ReferencePlane DownwardPlane(Document doc, XYZ point)
        {
            var half = Mm.ToFeet(500);
            var plane = doc.Create.NewReferencePlane(point - XYZ.BasisX * half, point + XYZ.BasisX * half, XYZ.BasisY, doc.ActiveView)
                ?? throw new InvalidOperationException("Revit returned no reference plane.");
            if (plane.Normal.Z > 0) plane.Flip();
            if (plane.Normal.Z > -0.999) throw new InvalidOperationException("could not make a horizontal reference plane facing down.");
            plane.Name = "B2M fit-out " + RevitCompat.GetId(plane.Id);
            return plane;
        }

        /// <summary>
        /// Some level-based families take the point's z (the level's elevation plus the offset) as their offset above the
        /// level, so they stand the level's elevation too high. Puts such an instance back at the height asked for (by its
        /// offset parameter, else by moving it down) and says so; null when it already stands right.
        /// </summary>
        private static string PutBackAtHeight(Document doc, FamilyInstance instance, Level level, XYZ point)
        {
            var wanted = Mm.FromFeet(point.Z - level.ProjectElevation);
            var offset = Commands.Families.MeasureFamilyTypesHandler.OffsetParameter(instance);
            var location = (instance.Location as LocationPoint)?.Point;
            var extra = FamilyMeasure.BaseOffsetMm(wanted, offset.HasValue ? Mm.FromFeet(offset.Value) : (double?)null,
                location != null ? Mm.FromFeet(location.Z - level.ProjectElevation) : (double?)null, Mm.FromFeet(level.ProjectElevation));
            if (extra == 0) return null;
            var down = Mm.ToFeet(extra);
            Parameter writable = null;
            foreach (var bip in new[] { BuiltInParameter.INSTANCE_ELEVATION_PARAM, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM })
            {
                var parameter = instance.get_Parameter(bip);
                if (parameter != null && !parameter.IsReadOnly && parameter.StorageType == StorageType.Double && parameter.HasValue
                    && Math.Abs(parameter.AsDouble() - (point.Z - level.ProjectElevation) - down) < Tolerance)
                {
                    writable = parameter;
                    break;
                }
            }
            if (writable != null) writable.Set(writable.AsDouble() - down);
            else ElementTransformUtils.MoveElement(doc, instance.Id, new XYZ(0, 0, -down));
            doc.Regenerate();
            return FormattableString.Invariant($"The family took the point's height as its offset above the level and stood {extra} mm too high; it was put back at the height asked for.");
        }

        /// <summary>Raises a work-plane-based instance off the level by its offset parameter.</summary>
        private static void SetOffset(FamilyInstance instance, double feet)
        {
            foreach (var bip in new[] { BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, BuiltInParameter.INSTANCE_ELEVATION_PARAM })
            {
                var parameter = instance.get_Parameter(bip);
                if (parameter == null || parameter.IsReadOnly) continue;
                parameter.Set(feet);
                return;
            }
            throw new InvalidOperationException("this family has no settable offset from its work plane.");
        }

        private static string Describe(XYZ p) =>
            FormattableString.Invariant($"{Mm.FromFeet(p.X)}, {Mm.FromFeet(p.Y)}, {Mm.FromFeet(p.Z)} mm");
    }
}
