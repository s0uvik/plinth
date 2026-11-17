// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), command get_report_history
// (listed in src/shared/Infrastructure/ToolReadPolicy.cs; served from src/shared/Reports/History), commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for Plinth: reads the last N records of %LOCALAPPDATA%\Plinth\reports.jsonl (written by report_task_result),
// newest first, instead of the SQLite history; unreadable lines are counted and skipped.

using System;
using System.IO;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Meta
{
    public sealed class GetReportHistoryHandler : IRevitCommand
    {
        private const int MaxLimit = 200;

        public string Name => "get_report_history";
        public string Description => "Read the most recent task reports saved by report_task_result, newest first.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject parameters)
        {
            var limit = parameters.Value<int?>("limit") ?? 20;
            if (limit < 1 || limit > MaxLimit) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to " + MaxLimit + ".");

            try
            {
                var reports = ReportLog.ReadLast(limit, out var total, out var skipped);
                return CommandResult.Ok(new JObject
                {
                    ["total"] = total,
                    ["returned"] = reports.Count,
                    ["unreadable_lines"] = skipped,
                    ["reports"] = new JArray(reports)
                });
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return CommandResult.Fail("REPORT_HISTORY_UNREADABLE: reports.jsonl could not be read: " + ex.Message);
            }
        }
    }
}
