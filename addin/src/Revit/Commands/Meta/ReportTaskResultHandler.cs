// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Reports/ReportTaskResultHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: the report (title, summary, status, optional details, timestamp, document title, Revit year) is
// appended to %LOCALAPPDATA%\Plinth\reports.jsonl and its summary shown in a Revit TaskDialog. rvt-mcp's WPF Reports
// window, SQLite store, change buffer, call-id binding, element resolution and toasts are not ported.
// Cannot run inside batch_execute (BatchExecutor).

using System;
using System.Globalization;
using System.IO;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Meta
{
    public sealed class ReportTaskResultHandler : IRevitCommand
    {
        private const int MaxTitle = 200;
        private const int MaxSummary = 4000;
        private const int MaxDetails = 20000;

        public string Name => "report_task_result";
        public string Description => "Record the result of a finished task (title, summary, status) in the Plinth report history "
            + "and show the summary to the user in a Revit dialog. Revit waits until the user closes it.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""title"":{""type"":""string""},
            ""summary"":{""type"":""string""},
            ""status"":{""type"":""string"",""enum"":[""completed"",""partial"",""failed"",""cancelled""]},
            ""details"":{""type"":""string""}},""required"":[""title"",""summary"",""status""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject parameters)
        {
            var title = parameters.Value<string>("title");
            var summary = parameters.Value<string>("summary");
            var status = parameters.Value<string>("status");
            var details = parameters.Value<string>("details");
            if (string.IsNullOrWhiteSpace(title) || title.Length > MaxTitle)
                return CommandResult.Fail("INVALID_PARAMS: title must be 1 to " + MaxTitle + " characters.");
            if (string.IsNullOrWhiteSpace(summary) || summary.Length > MaxSummary)
                return CommandResult.Fail("INVALID_PARAMS: summary must be 1 to " + MaxSummary + " characters.");
            if (details != null && details.Length > MaxDetails)
                return CommandResult.Fail("INVALID_PARAMS: details must be at most " + MaxDetails + " characters.");

            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            var record = new JObject
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["timestamp"] = timestamp,
                ["title"] = title,
                ["summary"] = summary,
                ["status"] = status,
                ["document_title"] = app.ActiveUIDocument?.Document?.Title,
                ["revit_year"] = app.Application.VersionNumber
            };
            if (details != null) record["details"] = details;

            try
            {
                ReportLog.Append(record);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return CommandResult.Fail("REPORT_SAVE_FAILED: reports.jsonl could not be written: " + ex.Message + " The report was not shown.");
            }

            var dialog = new TaskDialog("Plinth report")
            {
                MainInstruction = title,
                MainContent = summary,
                FooterText = "Status: " + status,
                CommonButtons = TaskDialogCommonButtons.Close
            };
            dialog.Show();

            return CommandResult.Ok(new JObject
            {
                ["saved"] = true,
                ["shown"] = true,
                ["id"] = record["id"],
                ["timestamp"] = timestamp
            });
        }
    }
}
