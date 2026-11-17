// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// files src/shared/Handlers/Rooms/ListRoomsHandler.cs and GetRoomBoundariesHandler.cs (status classification),
// commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: the classification both handlers repeated now lives here once.

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace Plinth.Revit.Commands
{
    internal static class RoomStatus
    {
        /// <summary>"placed", "not_enclosed" or "unplaced".</summary>
        public static string Of(Room room)
        {
            if (!(room.Location is LocationPoint)) return "unplaced";
            var boundary = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
            return room.Area > 0.0001 && boundary != null && boundary.Count > 0 ? "placed" : "not_enclosed";
        }
    }
}
