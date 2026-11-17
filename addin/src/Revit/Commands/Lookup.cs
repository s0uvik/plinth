using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands
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

        /// <summary>A view family type of the given family by exact name, else the project default for the group, else the first one.</summary>
        public static bool TryViewFamilyType(Document doc, ViewFamily family, ElementTypeGroup defaultGroup, string typeName,
            out ViewFamilyType type, out CommandResult failure)
        {
            failure = null;
            var types = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .Where(t => t.ViewFamily == family).OrderBy(t => t.Name).ToList();
            if (!string.IsNullOrEmpty(typeName))
                type = types.FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));
            else
                type = doc.GetElement(doc.GetDefaultElementTypeId(defaultGroup)) as ViewFamilyType ?? types.FirstOrDefault();
            if (type != null && type.ViewFamily == family) return true;
            type = null;
            failure = CommandResult.Fail("TYPE_NOT_FOUND: " + (string.IsNullOrEmpty(typeName)
                    ? "the project has no " + family + " view type."
                    : "no " + family + " view type named '" + typeName + "'."),
                new JObject { ["valid_view_types"] = new JArray(types.Take(MaxOptions).Select(t => t.Name)) });
            return false;
        }

        /// <summary>A view (not a template) by element ID. Fully qualified: Plinth.Revit.Commands.View is a namespace.</summary>
        public static bool TryView(Document doc, long id, out Autodesk.Revit.DB.View view, out CommandResult failure)
        {
            if (!TryElement(doc, id, "view", out view, out failure)) return false;
            if (!view.IsTemplate) return true;
            failure = CommandResult.Fail("VIEW_IS_TEMPLATE: view " + id + " ('" + view.Name + "') is a view template.");
            view = null;
            return false;
        }

        /// <summary>A sheet by ID, else by sheet number, else the active view when that is a sheet. Placeholder sheets are refused.</summary>
        public static bool TrySheet(Document doc, long? sheetId, string sheetNumber, out ViewSheet sheet, out CommandResult failure)
        {
            failure = null;
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .OrderBy(s => s.SheetNumber).ToList();
            string miss;
            if (sheetId.HasValue)
            {
                sheet = doc.GetElement(RevitCompat.ToElementId(sheetId.Value)) as ViewSheet;
                miss = "no sheet has id " + sheetId.Value + ".";
            }
            else if (!string.IsNullOrEmpty(sheetNumber))
            {
                sheet = sheets.FirstOrDefault(s => string.Equals(s.SheetNumber, sheetNumber, StringComparison.OrdinalIgnoreCase));
                miss = "no sheet numbered '" + sheetNumber + "'.";
            }
            else
            {
                sheet = doc.ActiveView as ViewSheet;
                miss = "give sheet_id or sheet_number (the active view is not a sheet).";
            }
            if (sheet != null && sheet.IsPlaceholder)
            {
                failure = CommandResult.Fail("SHEET_IS_PLACEHOLDER: sheet " + sheet.SheetNumber + " is a placeholder sheet and holds no views.");
                sheet = null;
                return false;
            }
            if (sheet != null) return true;
            failure = CommandResult.Fail("SHEET_NOT_FOUND: " + miss, new JObject
            {
                ["valid_sheets"] = new JArray(sheets.Where(s => !s.IsPlaceholder).Take(MaxOptions).Select(s => new JObject
                {
                    ["id"] = RevitCompat.GetId(s.Id),
                    ["number"] = s.SheetNumber,
                    ["name"] = s.Name
                }))
            });
            return false;
        }

        /// <summary>A title block type by ID, or by "Type" or "Family: Type" name.</summary>
        public static bool TryTitleBlock(Document doc, long? typeId, string name, out FamilySymbol symbol, out CommandResult failure)
        {
            failure = null;
            var all = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType()
                .OfType<FamilySymbol>().OrderBy(s => s.FamilyName).ThenBy(s => s.Name).ToList();
            List<FamilySymbol> matches;
            if (typeId.HasValue)
                matches = all.Where(s => RevitCompat.GetId(s.Id) == typeId.Value).ToList();
            else
                matches = all.Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.FamilyName + ": " + s.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
            {
                symbol = matches[0];
                return true;
            }
            symbol = null;
            failure = CommandResult.Fail(matches.Count == 0
                    ? "TITLEBLOCK_NOT_FOUND: no title block type " + (typeId.HasValue ? "has id " + typeId.Value : "named '" + name + "'") + "."
                    : "TITLEBLOCK_AMBIGUOUS: several title block families have a type named '" + name + "'; use 'Family: Type' or the id.",
                new JObject { ["valid_title_blocks"] = TitleBlocks(all) });
            return false;
        }

        public static JArray TitleBlocks(IEnumerable<FamilySymbol> symbols) =>
            new JArray(symbols.Take(MaxOptions).Select(s => new JObject
            {
                ["id"] = RevitCompat.GetId(s.Id),
                ["name"] = s.FamilyName + ": " + s.Name
            }));

        /// <summary>A schedule (not a template or a title block revision schedule) by ID or by exact name.</summary>
        public static bool TrySchedule(Document doc, long? id, string name, out ViewSchedule schedule, out CommandResult failure)
        {
            failure = null;
            var all = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule).OrderBy(s => s.Name).ToList();
            schedule = id.HasValue
                ? all.FirstOrDefault(s => RevitCompat.GetId(s.Id) == id.Value)
                : all.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (schedule != null) return true;
            failure = CommandResult.Fail("SCHEDULE_NOT_FOUND: " + (id.HasValue
                    ? "no placeable schedule has id " + id.Value + "."
                    : string.IsNullOrEmpty(name) ? "give schedule_id or schedule_name." : "no schedule named '" + name + "'."),
                new JObject
                {
                    ["valid_schedules"] = new JArray(all.Take(MaxOptions).Select(s => new JObject
                    {
                        ["id"] = RevitCompat.GetId(s.Id),
                        ["name"] = s.Name
                    }))
                });
            return false;
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
