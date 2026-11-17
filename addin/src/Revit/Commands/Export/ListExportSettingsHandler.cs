// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Export/ListExportSettingsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: ids are numbers; lists are sorted by name; rvt-mcp's ResponsePaging is replaced by
// ExportHelpers.TryPaging (same start_index / max_results, max 500); read errors are no longer swallowed
// item by item.

using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Export
{
    public sealed class ListExportSettingsHandler : IRevitCommand
    {
        public string Name => "list_export_settings";
        public string Description =>
            "List saved DWG export setups (use the name as export_dwg settings_name), named print settings and view/sheet sets "
            + "(with their view count). kind_filter: all, dwg, print or view_sheet_set. Paged with start_index / max_results (default 50, max 500).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""kind_filter"":{""type"":""string"",""enum"":[""all"",""dwg"",""print"",""view_sheet_set""]}," + ExportHelpers.PagingSchema + @"}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!ExportHelpers.TryPaging(p, 50, 500, out var start, out var max, out var failure)) return failure;
            var kind = p.Value<string>("kind_filter") ?? "all";

            var dwg = kind == "all" || kind == "dwg"
                ? Named<ExportDWGSettings>(doc).Select(s => new JObject { ["id"] = RevitCompat.GetId(s.Id), ["name"] = s.Name }).ToList()
                : new List<JObject>();
            var print = kind == "all" || kind == "print"
                ? Named<PrintSetting>(doc).Select(s => new JObject { ["id"] = RevitCompat.GetId(s.Id), ["name"] = s.Name }).ToList()
                : new List<JObject>();
            var sets = kind == "all" || kind == "view_sheet_set"
                ? Named<ViewSheetSet>(doc).Select(s => new JObject
                {
                    ["id"] = RevitCompat.GetId(s.Id),
                    ["name"] = s.Name,
                    ["view_count"] = s.Views?.Size ?? 0
                }).ToList()
                : new List<JObject>();

            var longest = new[] { dwg.Count, print.Count, sets.Count }.Max();
            return CommandResult.Ok(new
            {
                kind_filter = kind,
                start_index = start,
                max_results = max,
                counts = new { dwg = dwg.Count, print = print.Count, view_sheet_set = sets.Count },
                truncated = start + max < longest,
                next_index = start + max < longest ? (int?)(start + max) : null,
                dwg_export_settings = dwg.Skip(start).Take(max),
                print_settings = print.Skip(start).Take(max),
                view_sheet_sets = sets.Skip(start).Take(max)
            });
        }

        private static IEnumerable<T> Named<T>(Document doc) where T : Element =>
            new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().OrderBy(e => e.Name);
    }
}
