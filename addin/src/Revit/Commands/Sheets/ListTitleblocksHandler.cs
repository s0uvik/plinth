// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Sheets/ListTitleblocksHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: a limit outside 1..10000 is an error (rvt-mcp clamped it); results are ordered by family
// and type name and flag the project's default title block; total counts every match before the limit;
// the camelCase aliases are gone.

using System;
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
            "List loaded title block types ('Family: Type') with how many sheets use each. name_pattern filters (case-insensitive); "
            + "include_inactive (default true) keeps unused types; limit 1..10000 (default 1000).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""name_pattern"":{""type"":""string""},
            ""include_inactive"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}}}";
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

            var rows = new JArray(matches.Take(limit).Select(x => new JObject
            {
                ["type_id"] = RevitCompat.GetId(x.Symbol.Id),
                ["family_id"] = RevitCompat.GetIdOrNull(x.Symbol.Family?.Id),
                ["family_name"] = x.Symbol.FamilyName ?? "",
                ["type_name"] = x.Symbol.Name ?? "",
                ["display_name"] = x.Display,
                ["is_default"] = x.Symbol.Id == defaultId,
                ["is_active"] = x.Count > 0,
                ["sheet_instance_count"] = x.Count
            }));

            return CommandResult.Ok(new JObject
            {
                ["total"] = matches.Count,
                ["returned"] = rows.Count,
                ["limit_hit"] = matches.Count > limit,
                ["titleblocks"] = rows
            });
        }
    }
}
