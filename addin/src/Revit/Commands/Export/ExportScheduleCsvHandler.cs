// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Schedules/ExportScheduleCsvHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: written to the exact path asked for (output_path, or output_folder + file_name; .csv
// added unless the name ends in .txt) through a staging folder, so an existing file is replaced only with
// overwrite (rvt-mcp let Revit overwrite it); output_folder is created when missing; the schedule is found
// with the shared lookup, whose miss lists valid schedules; the delimiter must be one character.
// No transaction is opened.

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Export
{
    public sealed class ExportScheduleCsvHandler : IRevitCommand
    {
        public string Name => "export_schedule_csv";
        public string Description =>
            "Export a schedule's rows (by schedule_id or schedule_name) to a delimited text file at output_path, or output_folder + file_name "
            + "(absolute; .csv added unless the name ends in .txt; folder created if missing). delimiter: one character, default ','. "
            + "Values are double-quoted. An existing file is replaced only with overwrite: true.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + ExportHelpers.OutputSchema + @",
            ""schedule_id"":{""type"":""integer""},
            ""schedule_name"":{""type"":""string""},
            ""delimiter"":{""type"":""string""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var delimiter = p.Value<string>("delimiter") ?? ",";
            if (delimiter.Length != 1) return CommandResult.Fail("INVALID_PARAMS: delimiter must be exactly one character, such as ',' ';' or a tab.");
            var id = p.Value<long?>("schedule_id");
            var name = p.Value<string>("schedule_name");
            if (id.HasValue && !string.IsNullOrEmpty(name)) return CommandResult.Fail("INVALID_PARAMS: pass schedule_id or schedule_name, not both.");
            if (!Lookup.TrySchedule(doc, id, name, out var schedule, out var failure)) return failure;

            var overwrite = p.Value<bool?>("overwrite") ?? false;
            var path = p.Value<string>("output_path");
            var fileName = p.Value<string>("file_name");
            var extension = (path ?? fileName ?? "").EndsWith(".txt", System.StringComparison.OrdinalIgnoreCase) ? ".txt" : ".csv";
            if (!OutputFile.TryResolve(path, p.Value<string>("output_folder"), fileName, extension, overwrite, out var target, out failure)) return failure;

            return ExportHelpers.ExportSingle("csv", target, "export" + extension, overwrite, staging =>
            {
                var options = new ViewScheduleExportOptions
                {
                    FieldDelimiter = delimiter,
                    TextQualifier = ExportTextQualifier.DoubleQuote
                };
                schedule.Export(staging, "export" + extension, options);
            }, new { schedule_id = RevitCompat.GetId(schedule.Id), schedule_name = schedule.Name });
        }
    }
}
