// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Schedules/GetScheduleDataHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: schedule misses list the valid schedules; startRow/maxRows limits are errors with the
// allowed range; nextRow tells the caller where the next page starts; column names skip hidden fields (rvt-mcp
// paired columns with every field, so a hidden field shifted the headings); the cell-meta and row-kind logic
// is unchanged. Cell texts are Revit's formatted display strings in the project's units (not converted to mm).

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Schedule
{
    public sealed class GetScheduleDataHandler : IRevitCommand
    {
        private const int MaxRowsLimit = 500;

        public string Name => "get_schedule_data";
        public string Description =>
            "Read a schedule's rendered table (scheduleId or scheduleName): column headings and body rows as Revit displays them "
            + "(project display units), with rowKinds (header/data/text/blank). Paged: startRow (default 0), maxRows (1-500, default 200). "
            + "includeCellMeta adds cell type and merged-cell anchors.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + ScheduleHelpers.ScheduleRefSchema + @",
            ""startRow"":{""type"":""integer""},
            ""maxRows"":{""type"":""integer""},
            ""includeCellMeta"":{""type"":""boolean""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var startRow = p.Value<int?>("startRow") ?? 0;
            var maxRows = p.Value<int?>("maxRows") ?? 200;
            var includeCellMeta = p.Value<bool?>("includeCellMeta") ?? false;
            if (startRow < 0) return CommandResult.Fail("INVALID_PARAMS: startRow must be 0 or more.");
            if (maxRows < 1 || maxRows > MaxRowsLimit) return CommandResult.Fail("INVALID_PARAMS: maxRows must be 1 to " + MaxRowsLimit + ".");
            if (!ScheduleHelpers.TryResolve(doc, p, out var schedule, out var failure)) return failure;

            TableSectionData header, body;
            try
            {
                var table = schedule.GetTableData();
                header = table.GetSectionData(SectionType.Header);
                body = table.GetSectionData(SectionType.Body);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("TABLE_UNAVAILABLE: could not read the schedule's table: " + ex.Message);
            }
            if (body == null) return CommandResult.Fail("TABLE_UNAVAILABLE: the schedule has no body section.");

            string warning = null;
            const string CellWarning = "Some cells failed to render and were returned as empty strings.";
            var nCols = body.NumberOfColumns;
            var nRows = body.NumberOfRows;
            var headerRows = header?.NumberOfRows ?? 0;

            // Column names from the visible fields, then from the last header row.
            var definition = schedule.Definition;
            var visible = definition.GetFieldOrder().Select(definition.GetField).Where(f => !f.IsHidden).ToList();
            var columns = new string[nCols];
            for (var c = 0; c < nCols; c++)
            {
                var text = string.Empty;
                try
                {
                    if (c < visible.Count) text = string.IsNullOrEmpty(visible[c].ColumnHeading) ? visible[c].GetName() : visible[c].ColumnHeading;
                    if (string.IsNullOrEmpty(text) && headerRows > 0) text = header.GetCellText(headerRows - 1, c) ?? string.Empty;
                }
                catch (Exception)
                {
                    warning = CellWarning;
                }
                columns[c] = text ?? string.Empty;
            }

            var start = Math.Min(startRow, nRows);
            var end = Math.Min(start + maxRows, nRows);
            var rows = new JArray();
            var rowKinds = new JArray();
            var cellMeta = includeCellMeta ? new JArray() : null;
            for (var r = start; r < end; r++)
            {
                var row = new string[nCols];
                var textOnly = true;
                var meta = includeCellMeta ? new JArray() : null;
                for (var c = 0; c < nCols; c++)
                {
                    try { row[c] = schedule.GetCellText(SectionType.Body, r, c) ?? string.Empty; }
                    catch (Exception)
                    {
                        row[c] = string.Empty;
                        warning = CellWarning;
                    }
                    CellType cellType = CellType.Text;
                    try { cellType = body.GetCellType(r, c); }
                    catch (Exception) { }
                    if (cellType != CellType.Text) textOnly = false;

                    if (includeCellMeta)
                    {
                        var m = new JObject { ["cellType"] = cellType.ToString(), ["isMerged"] = false };
                        try
                        {
                            var merged = body.GetMergedCell(r, c);
                            if (merged != null && (merged.Bottom > merged.Top || merged.Right > merged.Left))
                            {
                                m["isMerged"] = true;
                                if (r == merged.Top && c == merged.Left)
                                    m["mergeAnchor"] = new JObject { ["row"] = merged.Top, ["col"] = merged.Left };
                            }
                        }
                        catch (Exception) { }
                        meta.Add(m);
                    }
                }
                rows.Add(new JArray(row));
                rowKinds.Add(row.All(string.IsNullOrWhiteSpace) ? "blank"
                    : textOnly && row.SequenceEqual(columns) ? "header"
                    : textOnly ? "text" : "data");
                cellMeta?.Add(meta);
            }

            var truncated = end < nRows;
            var result = new JObject
            {
                ["scheduleId"] = RevitCompat.GetId(schedule.Id),
                ["scheduleName"] = schedule.Name,
                ["columns"] = new JArray(columns),
                ["totalRows"] = nRows,
                ["startRow"] = startRow,
                ["returnedRows"] = rows.Count,
                ["truncated"] = truncated,
                ["nextRow"] = truncated ? end : (int?)null,
                ["rows"] = rows,
                ["rowKinds"] = rowKinds,
                ["warning"] = warning
            };
            if (includeCellMeta) result["cellMeta"] = cellMeta;
            return CommandResult.Ok(result);
        }
    }
}
