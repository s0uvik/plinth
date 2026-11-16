// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Sheets/ListTitleblocksHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: a limit outside 1..10000 is an error (rvt-mcp clamped it); results are ordered by family
// and type name and flag the project's default title block; total counts every match before the limit;
// the camelCase aliases are gone.
// Step 9 T10b: each title block type reports its sheet size (sheet_width_mm, sheet_height_mm, sheet_size_source), from
// the type's Sheet Width/Sheet Height parameters, else a placed instance of the type (the read-only SHEET_WIDTH /
// SHEET_HEIGHT instance parameters), else the family's own Sheet Width/Height for that type read from the family
// document (EditFamily, opened read-only and closed unsaved; read_family_documents false skips this). When none
// gives it, the size is null with sheet_size_reason.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Sheets
{
    public sealed class ListTitleblocksHandler : IRevitCommand
    {
        public string Name => "list_titleblocks";
        public string Description =>
            "List loaded title block types ('Family: Type') with how many sheets use each and the sheet size in mm "
            + "(sheet_width_mm/sheet_height_mm from the type, a placed instance or the family; null with sheet_size_reason when unknown). "
            + "name_pattern filters (case-insensitive); include_inactive (default true) keeps unused types; limit 1..10000 (default 1000); "
            + "read_family_documents (default true) lets unplaced types be sized from their family.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""name_pattern"":{""type"":""string""},
            ""include_inactive"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""},
            ""read_family_documents"":{""type"":""boolean""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var pattern = p.Value<string>("name_pattern");
            var includeInactive = p.Value<bool?>("include_inactive") ?? true;
            var limit = p.Value<int?>("limit") ?? 1000;
            if (limit < 1 || limit > 10000) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to 10000.");

            var counts = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType()
                .Select(e => RevitCompat.GetId(e.GetTypeId())).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
            var defaultId = doc.GetDefaultFamilyTypeId(new ElementId(BuiltInCategory.OST_TitleBlocks));

            var matches = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType()
                .OfType<FamilySymbol>()
                .OrderBy(s => s.FamilyName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s =>
                {
                    counts.TryGetValue(RevitCompat.GetId(s.Id), out var count);
                    return new { Symbol = s, Count = count, Display = s.FamilyName + ": " + s.Name };
                })
                .Where(x => includeInactive || x.Count > 0)
                .Where(x => SheetsHelpers.ContainsIgnoreCase(x.Display, pattern))
                .ToList();

            var page = matches.Take(limit).ToList();
            var sizes = SheetSizes(doc, page.Select(x => x.Symbol).ToList(), p.Value<bool?>("read_family_documents") ?? true);
            var rows = new JArray(page.Select(x =>
            {
                var size = sizes[RevitCompat.GetId(x.Symbol.Id)];
                return new JObject
                {
                    ["type_id"] = RevitCompat.GetId(x.Symbol.Id),
                    ["family_id"] = RevitCompat.GetIdOrNull(x.Symbol.Family?.Id),
                    ["family_name"] = x.Symbol.FamilyName ?? "",
                    ["type_name"] = x.Symbol.Name ?? "",
                    ["display_name"] = x.Display,
                    ["is_default"] = x.Symbol.Id == defaultId,
                    ["is_active"] = x.Count > 0,
                    ["sheet_instance_count"] = x.Count,
                    ["sheet_width_mm"] = size.WidthMm,
                    ["sheet_height_mm"] = size.HeightMm,
                    ["sheet_size_source"] = size.Source,
                    ["sheet_size_reason"] = size.Reason
                };
            }));

            return CommandResult.Ok(new JObject
            {
                ["total"] = matches.Count,
                ["returned"] = rows.Count,
                ["limit_hit"] = matches.Count > limit,
                ["titleblocks"] = rows
            });
        }

        private sealed class SheetSize
        {
            public double? WidthMm;
            public double? HeightMm;
            public string Source;
            public string Reason;
        }

        /// <summary>Sheet Width / Sheet Height on an element (built-in parameters first, then by name), in feet.</summary>
        private static bool TrySize(Element element, out double width, out double height)
        {
            width = Value(element.get_Parameter(BuiltInParameter.SHEET_WIDTH) ?? element.LookupParameter("Sheet Width"));
            height = Value(element.get_Parameter(BuiltInParameter.SHEET_HEIGHT) ?? element.LookupParameter("Sheet Height"));
            return width > 0 && height > 0;
        }

        private static double Value(Parameter parameter) =>
            parameter != null && parameter.HasValue && parameter.StorageType == StorageType.Double ? parameter.AsDouble() : 0;

        private static SheetSize Known(double widthFeet, double heightFeet, string source) =>
            new SheetSize { WidthMm = Mm.FromFeet(widthFeet), HeightMm = Mm.FromFeet(heightFeet), Source = source };

        /// <summary>Sizes by type id: type parameters, then a placed instance, then the family document (one open per family).</summary>
        private static Dictionary<long, SheetSize> SheetSizes(Document doc, List<FamilySymbol> symbols, bool readFamilies)
        {
            var result = new Dictionary<long, SheetSize>();
            var instances = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType()
                .ToElements().GroupBy(e => RevitCompat.GetId(e.GetTypeId())).ToDictionary(g => g.Key, g => g.ToList());
            var unsized = new List<FamilySymbol>();
            foreach (var symbol in symbols)
            {
                var id = RevitCompat.GetId(symbol.Id);
                if (TrySize(symbol, out var w, out var h))
                {
                    result[id] = Known(w, h, "type parameters");
                    continue;
                }
                if (instances.TryGetValue(id, out var placed))
                {
                    var sized = placed.FirstOrDefault(e => TrySize(e, out _, out _));
                    if (sized != null && TrySize(sized, out w, out h))
                    {
                        result[id] = Known(w, h, "placed title block " + RevitCompat.GetId(sized.Id));
                        continue;
                    }
                }
                unsized.Add(symbol);
            }

            foreach (var group in unsized.GroupBy(s => s.Family?.Id ?? ElementId.InvalidElementId))
            {
                var family = group.First().Family;
                string reason = null;
                Dictionary<string, double[]> byType = null;
                if (!readFamilies) reason = "no Sheet Width/Height on the type and it is not placed on a sheet; read_family_documents is false";
                else if (family == null || !family.IsEditable) reason = "no Sheet Width/Height on the type, it is not placed on a sheet, and its family cannot be opened";
                else byType = FamilySizes(doc, family, out reason);
                foreach (var symbol in group)
                {
                    var id = RevitCompat.GetId(symbol.Id);
                    if (byType != null && byType.TryGetValue(symbol.Name, out var size))
                        result[id] = Known(size[0], size[1], "family " + family.Name);
                    else
                        result[id] = new SheetSize
                        {
                            Reason = reason ?? "the family's Sheet Width/Height are not set for type '" + symbol.Name + "'"
                        };
                }
            }
            return result;
        }

        /// <summary>
        /// Opens the family document (never saved) and reads Sheet Width/Height for each of its types, in feet.
        /// Null with a reason when the family has no such parameters or cannot be opened.
        /// </summary>
        private static Dictionary<string, double[]> FamilySizes(Document doc, Family family, out string reason)
        {
            reason = null;
            Document familyDoc = null;
            try
            {
                familyDoc = doc.EditFamily(family);
                var manager = familyDoc.FamilyManager;
                var width = manager.get_Parameter(BuiltInParameter.SHEET_WIDTH) ?? manager.get_Parameter("Sheet Width");
                var height = manager.get_Parameter(BuiltInParameter.SHEET_HEIGHT) ?? manager.get_Parameter("Sheet Height");
                if (width == null || height == null)
                {
                    reason = "family '" + family.Name + "' has no Sheet Width/Sheet Height parameters";
                    return null;
                }
                var sizes = new Dictionary<string, double[]>(StringComparer.Ordinal);
                foreach (FamilyType type in manager.Types)
                {
                    var w = type.HasValue(width) ? type.AsDouble(width) ?? 0 : 0;
                    var h = type.HasValue(height) ? type.AsDouble(height) ?? 0 : 0;
                    if (w > 0 && h > 0 && !string.IsNullOrEmpty(type.Name)) sizes[type.Name] = new[] { w, h };
                }
                if (sizes.Count == 0) reason = "family '" + family.Name + "' gives no Sheet Width/Sheet Height values";
                return sizes;
            }
            catch (Exception ex)
            {
                reason = "family '" + family.Name + "' could not be read: " + ex.Message;
                return null;
            }
            finally
            {
                if (familyDoc != null)
                {
                    try { familyDoc.Close(false); }
                    catch { /* best effort: the copy opened by EditFamily is never saved back */ }
                }
            }
        }
    }
}
