// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Infrastructure/RevitCompat.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: Plinth supports Revit 2024 and later only, so the IntegerValue branches are gone.

using Autodesk.Revit.DB;

namespace Plinth.Revit.Bridge
{
    /// <summary>
    /// The one place Revit API year differences live. ElementId.Value exists from 2024;
    /// IntegerValue is gone from 2026, so nothing else may touch either directly.
    /// </summary>
    internal static class RevitCompat
    {
        public static long GetId(ElementId id) => id.Value;

        public static long? GetIdOrNull(ElementId id) => id?.Value;

        public static ElementId ToElementId(long id) => new ElementId(id);
    }
}
