using System;
using System.Linq;
using Autodesk.Revit.DB;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands
{
    /// <summary>
    /// Name and ID lookups shared by commands. A miss returns an error carrying the valid options,
    /// never a silent fallback (rvt-mcp used the first level when a name did not match).
    /// </summary>
    internal static class Lookup
    {
        private const int MaxOptions = 100;

        public static bool TryLevel(Document doc, string name, out Level level, out CommandResult failure)
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
            level = levels.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            failure = level != null ? null : CommandResult.Fail("LEVEL_NOT_FOUND: no level named '" + name + "'.",
                new JObject { ["valid_levels"] = new JArray(levels.Take(MaxOptions).Select(l => l.Name)) });
            return level != null;
        }

        /// <summary>By ID, else by exact name, else the document's default wall type.</summary>
        public static bool TryWallType(Document doc, long? typeId, string typeName, out WallType wallType, out CommandResult failure)
        {
            failure = null;
            if (typeId.HasValue)
            {
                wallType = doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as WallType;
                if (wallType == null) failure = WallTypeMiss(doc, "no wall type has id " + typeId.Value + ".");
                return wallType != null;
            }
            if (!string.IsNullOrEmpty(typeName))
            {
                wallType = WallTypes(doc).FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));
                if (wallType == null) failure = WallTypeMiss(doc, "no wall type named '" + typeName + "'.");
                return wallType != null;
            }
            wallType = doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.WallType)) as WallType;
            if (wallType == null) failure = WallTypeMiss(doc, "the project has no default wall type; pass typeName.");
            return wallType != null;
        }

        private static IOrderedEnumerable<WallType> WallTypes(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().OrderBy(t => t.Name);

        private static CommandResult WallTypeMiss(Document doc, string message) =>
            CommandResult.Fail("TYPE_NOT_FOUND: " + message, new JObject
            {
                ["valid_wall_types"] = new JArray(WallTypes(doc).Take(MaxOptions).Select(t => new JObject
                {
                    ["id"] = RevitCompat.GetId(t.Id),
                    ["name"] = t.Name,
                    ["kind"] = t.Kind.ToString()
                }))
            });
    }
}
