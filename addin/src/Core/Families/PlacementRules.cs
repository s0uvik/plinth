// New in B2M (Step 10, F5): create_point_based_element, create_lighting_fixture and create_air_terminal place a family
// in one of seven ways, chosen from the family's placement type (Revit's FamilyPlacementType name), the kind of host
// given and the options asked for. This is the Revit-free choice, so every refused combination is unit-tested.

namespace B2M.Revit.Core.Families
{
    public static class PlacementRules
    {
        public enum HostKind
        {
            None,
            Wall,

            /// <summary>A ceiling, floor or roof: a host with horizontal faces.</summary>
            Horizontal,
            Other
        }

        public enum Mode
        {
            /// <summary>Level-based, free at a point: rotation and height allowed.</summary>
            Level,

            /// <summary>Hosted by a wall (doors, windows, wall-based fixtures): along the wall, at a height, on a side.</summary>
            WallHosted,

            /// <summary>Hosted by another host (a ceiling-hosted fixture).</summary>
            Hosted,

            /// <summary>Face-based on a wall's exterior or interior side face.</summary>
            WallFace,

            /// <summary>Face-based on a ceiling's bottom face or a floor's or roof's top face.</summary>
            HorizontalFace,

            /// <summary>Work-plane-based with no host: on the level's plane, facing up, raised by the offset.</summary>
            LevelPlane,

            /// <summary>Work-plane-based with no host: on a new reference plane at the height, facing down (lights, fans).</summary>
            FaceDownPlane
        }

        public sealed class Options
        {
            public bool HasHostOffset;
            public bool HasSide;
            public bool FaceDown;
            public bool HasRotation;

            /// <summary>An offset above the level or an absolute z was given.</summary>
            public bool HasHeight;
        }

        /// <summary>The placement mode, or null with a "CODE: message" error. warning is set when an input is ignored.</summary>
        public static Mode? Choose(string placementType, HostKind host, Options o, out string error, out string warning)
        {
            error = null;
            warning = null;
            switch (placementType)
            {
                case "OneLevelBased":
                    if (o.HasHostOffset) return Fail("INVALID_PARAMS: this family is not hosted; give x and y, not a host offset.", out error);
                    if (o.HasSide) return Fail("INVALID_PARAMS: a wall side applies to wall-hosted and face-based families; this family is level-based.", out error);
                    if (o.FaceDown) return Fail("INVALID_PARAMS: face down applies to work-plane-based families; this family is level-based.", out error);
                    if (host != HostKind.None) warning = "host_id ignored: this family does not use a host.";
                    return Mode.Level;

                case "OneLevelBasedHosted":
                    if (host == HostKind.None) return Fail("HOST_REQUIRED: this family is hosted; give host_id. Nothing was created.", out error);
                    if (o.HasRotation) return Fail("INVALID_PARAMS: a hosted family follows its host; rotation is not allowed.", out error);
                    if (o.FaceDown) return Fail("INVALID_PARAMS: face down applies to work-plane-based families without a host.", out error);
                    if (host != HostKind.Wall && (o.HasSide || o.HasHostOffset))
                        return Fail("HOST_NOT_WALL: a wall side and a host offset need a wall as host.", out error);
                    return host == HostKind.Wall ? Mode.WallHosted : Mode.Hosted;

                case "WorkPlaneBased":
                    switch (host)
                    {
                        case HostKind.Wall:
                            if (!o.HasSide)
                                return Fail("SIDE_REQUIRED: a face-based family on a wall needs the side: wallSide exterior or interior, or a "
                                    + "toward point on that side.", out error);
                            if (o.HasRotation) return Fail("INVALID_PARAMS: on a wall face the family stays upright; rotation is not allowed.", out error);
                            if (o.FaceDown) return Fail("INVALID_PARAMS: face down applies only without a host.", out error);
                            return Mode.WallFace;
                        case HostKind.Horizontal:
                            if (o.HasHostOffset || o.HasSide)
                                return Fail("INVALID_PARAMS: a host offset and a wall side apply to walls; give x and y on the ceiling, floor or roof.", out error);
                            if (o.FaceDown) return Fail("INVALID_PARAMS: face down applies only without a host.", out error);
                            if (o.HasHeight)
                                return Fail("INVALID_PARAMS: on a ceiling, floor or roof the height is the host face's; omit z and offset.", out error);
                            return Mode.HorizontalFace;
                        case HostKind.None:
                            if (o.HasHostOffset || o.HasSide)
                                return Fail("INVALID_PARAMS: a host offset and a wall side need host_id of a wall.", out error);
                            return o.FaceDown ? Mode.FaceDownPlane : Mode.LevelPlane;
                        default:
                            return Fail("UNSUPPORTED_HOST: a face-based family is placed on a wall, ceiling, floor or roof, or without a host.", out error);
                    }

                default:
                    return Fail("UNSUPPORTED_PLACEMENT: placement type " + placementType + " is not supported. Nothing was created.", out error);
            }
        }

        private static Mode? Fail(string message, out string error)
        {
            error = message;
            return null;
        }
    }
}
