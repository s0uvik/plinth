// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Sheets/GetPrintSettingsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: ids are numbers; lists are sorted by name; view/sheet sets carry their view count;
// rvt-mcp's ResponsePaging is replaced by ExportHelpers.TryPaging (same start_index / max_results, max 500).

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Export
{
    public sealed class GetPrintSettingsHandler : IRevitCommand
    {
        public string Name => "get_print_settings";
        public string Description =>
            "Report the print manager's current state (printer, print-to-file, print range) and list named print settings "
            + "(paper size, orientation) and view/sheet sets. kind_filter: all, print or view_sheet_set. Paged with start_index / max_results (default 50, max 500).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""kind_filter"":{""type"":""string"",""enum"":[""all"",""print"",""view_sheet_set""]}," + ExportHelpers.PagingSchema + @"}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!ExportHelpers.TryPaging(p, 50, 500, out var start, out var max, out var failure)) return failure;
            var kind = p.Value<string>("kind_filter") ?? "all";

            var pm = doc.PrintManager;
            var print = kind == "all" || kind == "print"
                ? new FilteredElementCollector(doc).OfClass(typeof(PrintSetting)).Cast<PrintSetting>().OrderBy(s => s.Name)
                    .Select(s => new JObject
                    {
                        ["id"] = RevitCompat.GetId(s.Id),
                        ["name"] = s.Name,
                        ["paper_size"] = Try(() => s.PrintParameters?.PaperSize?.Name),
                        ["orientation"] = Try(() => s.PrintParameters?.PageOrientation.ToString())
                    }).ToList()
                : new List<JObject>();
            var sets = kind == "all" || kind == "view_sheet_set"
                ? new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>().OrderBy(s => s.Name)
                    .Select(s => new JObject
                    {
                        ["id"] = RevitCompat.GetId(s.Id),
                        ["name"] = s.Name,
                        ["view_count"] = s.Views?.Size ?? 0
                    }).ToList()
                : new List<JObject>();

            var longest = Math.Max(print.Count, sets.Count);
            return CommandResult.Ok(new
            {
                selected_printer = Try(() => pm.PrinterName),
                print_to_file = Try(() => (bool?)pm.PrintToFile),
                print_range = Try(() => pm.PrintRange.ToString()),
                kind_filter = kind,
                start_index = start,
                max_results = max,
                counts = new { print = print.Count, view_sheet_set = sets.Count },
                truncated = start + max < longest,
                next_index = start + max < longest ? (int?)(start + max) : null,
                named_print_settings = print.Skip(start).Take(max),
                view_sheet_sets = sets.Skip(start).Take(max)
            });
        }

        // Some print properties throw when no printer is configured; report those as null.
        private static T Try<T>(Func<T> read)
        {
            try { return read(); }
            catch { return default(T); }
        }
    }
}
