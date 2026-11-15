using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands
{
    /// <summary>
    /// Name and ID lookups shared by commands. A miss returns an error carrying the valid options,
    /// never a silent fallback (rvt-mcp used the first level or type when a name did not match).
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

        /// <summary>A system type (wall, floor) by ID, else by exact name, else the document's default for the group.</summary>
        public static bool TryElementType<T>(Document doc, long? typeId, string typeName, ElementTypeGroup defaultGroup, string what,
            out T type, out CommandResult failure) where T : ElementType
        {
            failure = null;
            if (typeId.HasValue && !string.IsNullOrEmpty(typeName))
            {
                type = null;
                failure = CommandResult.Fail("INVALID_PARAMS: pass typeId or typeName, not both.");
                return false;
            }
            if (typeId.HasValue)
            {
                type = doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as T;
                if (type == null) failure = TypeMiss<T>(doc, what, "no " + what + " type has id " + typeId.Value + ".");
            }
            else if (!string.IsNullOrEmpty(typeName))
            {
                type = TypesOf<T>(doc).FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));
                if (type == null) failure = TypeMiss<T>(doc, what, "no " + what + " type named '" + typeName + "'.");
            }
            else
            {
                type = doc.GetElement(doc.GetDefaultElementTypeId(defaultGroup)) as T;
                if (type == null) failure = TypeMiss<T>(doc, what, "the project has no default " + what + " type; pass typeName.");
            }
            return type != null;
        }

        /// <summary>A loadable family type by ID, or by type name (plus family name and category to disambiguate).</summary>
        public static bool TryFamilySymbol(Document doc, long? typeId, string familyName, string typeName, BuiltInCategory? category,
            out FamilySymbol symbol, out CommandResult failure)
        {
            symbol = null;
            failure = null;
            if (typeId.HasValue)
            {
                symbol = doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as FamilySymbol;
                if (symbol == null) failure = CommandResult.Fail("TYPE_NOT_FOUND: no family type has id " + typeId.Value + ".");
                return symbol != null;
            }
            if (string.IsNullOrEmpty(typeName))
            {
                failure = CommandResult.Fail("INVALID_PARAMS: pass typeId, or typeName (with familyName if names repeat).");
                return false;
            }
            var candidates = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => category == null || (s.Category != null && s.Category.BuiltInCategory == category.Value))
                .ToList();
            var matches = candidates.Where(s => string.Equals(s.Name, typeName, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrEmpty(familyName) || string.Equals(s.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))).ToList();
            if (matches.Count == 1)
            {
                symbol = matches[0];
                return true;
            }
            failure = CommandResult.Fail(
                (matches.Count == 0 ? "TYPE_NOT_FOUND: no family type named '" : "TYPE_AMBIGUOUS: several families have a type named '")
                + typeName + "'" + (string.IsNullOrEmpty(familyName) ? "" : " in family '" + familyName + "'") + ".",
                new JObject { ["valid_types"] = Describe(matches.Count > 1 ? matches : candidates) });
            return false;
        }

        public static bool TryBuiltInCategory(string name, out BuiltInCategory category, out CommandResult failure)
        {
            failure = null;
            if (Enum.TryParse(name, false, out category) && Enum.IsDefined(typeof(BuiltInCategory), category)) return true;
            failure = CommandResult.Fail("INVALID_PARAMS: '" + name + "' is not a built-in category name such as OST_Doors, OST_Windows, OST_Walls, OST_Floors.");
            return false;
        }

        /// <summary>The language-independent category name, e.g. OST_Walls, or null.</summary>
        public static string BuiltInName(Category category) =>
            category == null || category.BuiltInCategory == BuiltInCategory.INVALID ? null : category.BuiltInCategory.ToString();

        public static bool TryElement<T>(Document doc, long id, string what, out T element, out CommandResult failure) where T : Element
        {
            element = doc.GetElement(RevitCompat.ToElementId(id)) as T;
            failure = element != null ? null : CommandResult.Fail("ELEMENT_NOT_FOUND: element " + id + " is not a " + what + " in this document.");
            return element != null;
        }

        private static IEnumerable<T> TypesOf<T>(Document doc) where T : ElementType =>
            new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().OrderBy(t => t.Name);

        private static CommandResult TypeMiss<T>(Document doc, string what, string message) where T : ElementType =>
            CommandResult.Fail("TYPE_NOT_FOUND: " + message, new JObject
            {
                ["valid_" + what + "_types"] = new JArray(TypesOf<T>(doc).Take(MaxOptions).Select(t => new JObject
                {
                    ["id"] = RevitCompat.GetId(t.Id),
                    ["name"] = t.Name
                }))
            });

        private static JArray Describe(IEnumerable<FamilySymbol> symbols) =>
            new JArray(symbols.OrderBy(s => s.FamilyName).ThenBy(s => s.Name).Take(MaxOptions).Select(s => new JObject
            {
                ["id"] = RevitCompat.GetId(s.Id),
                ["familyName"] = s.FamilyName,
                ["typeName"] = s.Name,
                ["builtInCategory"] = BuiltInName(s.Category)
            }));
    }
}
