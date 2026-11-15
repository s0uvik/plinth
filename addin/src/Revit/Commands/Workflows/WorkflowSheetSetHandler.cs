// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Workflows/WorkflowSheetSetHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: each sheet is made by create_sheet, views placed by place_view_on_sheet and schedules by
// place_schedule_on_sheet (called in-process, inside one transaction group), so the title block default and
// lookups match those tools; the title block is title_block_type_id or title_block_name (rvt-mcp:
// titleblock_type_id, else the first title block found); views and schedules go on a grid inside the title
// block frame (rvt-mcp used fixed foot offsets); unknown keys inside a sheet entry, views already on a sheet,
// and a view asked for twice are validation errors; renumber_strategy is dropped (rvt-mcp parsed it but never
// used it); rvt-mcp's FailureControl batch is replaced by FailureScope (any Revit warning rolls back); a failed
// parameter update marks the run partial; at most 100 sheets and 30 views or schedules per sheet.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using B2M.Revit.Commands.Sheets;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Workflows
{
    using View = Autodesk.Revit.DB.View;

    public sealed class WorkflowSheetSetHandler : IRevitCommand
    {
        private const int MaxSheets = 100;
        private const int MaxItemsPerSheet = 30;
        private static readonly string[] SheetKeys =
            { "sheet_number", "sheet_name", "title_block_type_id", "title_block_name", "view_ids", "schedule_ids", "parameters" };

        public string Name => "workflow_sheet_set";
        public string Description =>
            "Create a set of sheets. sheets: [{sheet_number, sheet_name, title_block_type_id or title_block_name, view_ids, schedule_ids, "
            + "parameters: {name: value}}] (lengths mm). Views and schedules are laid out on a grid in the title block. "
            + "dry_run (default true) only validates; continue_on_error keeps the sheets that worked.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""sheets"":{""type"":""array"",""items"":{""type"":""object""}},
            ""dry_run"":{""type"":""boolean""},
            ""continue_on_error"":{""type"":""boolean""}},
            ""required"":[""sheets""]}";
        public bool ReadOnly => false;

        private sealed class Spec
        {
            public string Number;
            public string Name;
            public long? TitleBlockId;
            public string TitleBlockName;
            public long[] ViewIds;
            public long[] ScheduleIds;
            public JObject Parameters;
        }

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var sheets = (JArray)p["sheets"];
            var dryRun = p.Value<bool?>("dry_run") ?? true;
            var continueOnError = p.Value<bool?>("continue_on_error") ?? false;
            if (sheets.Count == 0 || sheets.Count > MaxSheets) return CommandResult.Fail("INVALID_PARAMS: sheets must hold 1 to " + MaxSheets + " entries.");

            var specs = new List<Spec>();
            for (var i = 0; i < sheets.Count; i++)
            {
                if (!TryParse((JObject)sheets[i], i, out var spec, out var parseError)) return CommandResult.Fail("INVALID_PARAMS: " + parseError);
                specs.Add(spec);
            }

            var errors = Validate(doc, specs);
            var warnings = new List<string>();
            if (errors.Count > 0 && !continueOnError)
                return CommandResult.Fail("VALIDATION_FAILED: " + string.Join("; ", errors.Take(50)) + (errors.Count > 50 ? "; ..." : ""),
                    new JObject { ["errors"] = new JArray(errors.Take(200)) });
            warnings.AddRange(errors);
            var invalid = new HashSet<string>(errors.Select(e => e.Substring(6, e.IndexOf(':') - 6)), StringComparer.OrdinalIgnoreCase);

            var steps = new JArray
            {
                WorkflowsHelpers.Step("Validate Sheet Set", "workflow_sheet_set", errors.Count == 0 ? "succeeded" : "partial",
                    "Check sheet numbers, title blocks, views, schedules.", new { sheet_count = specs.Count, validation_error_count = errors.Count })
            };
            var reports = new JArray();
            var createdIds = new List<long>();
            var modifiedIds = new List<long>();

            if (dryRun)
            {
                foreach (var spec in specs)
                    reports.Add(new JObject
                    {
                        ["sheet_number"] = spec.Number,
                        ["sheet_name"] = spec.Name,
                        ["planned_viewports"] = spec.ViewIds.Length,
                        ["planned_schedules"] = spec.ScheduleIds.Length,
                        ["planned_parameter_updates"] = spec.Parameters.Count,
                        ["would_create"] = !invalid.Contains(spec.Number)
                    });
                steps.Add(WorkflowsHelpers.Step("Create Sheets", "create_sheet", "skipped", "Dry run: sheets validated, not created.", new { planned = specs.Count(s => !invalid.Contains(s.Number)) }));
                var dry = WorkflowsHelpers.Envelope(Name, true, errors.Count == 0 ? "succeeded" : "partial", steps, createdIds, modifiedIds, warnings,
                    WorkflowsHelpers.Rollback("TransactionGroup", false, "Dry run; nothing was written."));
                dry["sheets"] = reports;
                return CommandResult.Ok(dry);
            }

            var hadFailure = false;
            var partial = false;
            var rollback = WorkflowsHelpers.Rollback("TransactionGroup", false, "Committed.");
            using (var group = new TransactionGroup(doc, "B2M: Workflow sheet set"))
            {
                group.Start();
                foreach (var spec in specs.Where(s => !invalid.Contains(s.Number)))
                {
                    var before = createdIds.Count;
                    var error = CreateOne(app, doc, spec, createdIds, modifiedIds, out var report, ref partial);
                    reports.Add(report);
                    steps.Add(WorkflowsHelpers.Step("Create Sheet " + spec.Number, "create_sheet / place_view_on_sheet / place_schedule_on_sheet",
                        error == null ? "succeeded" : "failed", "Create the sheet, place its views and schedules, set its parameters.", report, error));
                    if (error == null) continue;
                    hadFailure = true;
                    if (!continueOnError) break;
                }

                if (hadFailure && !continueOnError)
                {
                    group.RollBack();
                    rollback = WorkflowsHelpers.Rollback("TransactionGroup", true, "A sheet failed and continue_on_error is false; nothing was changed.");
                    createdIds.Clear();
                    modifiedIds.Clear();
                }
                else if (group.Assimilate() != TransactionStatus.Committed)
                {
                    return CommandResult.Fail("NOT_COMMITTED: Revit did not commit the sheet set; nothing was changed.");
                }
            }

            var final = WorkflowsHelpers.Envelope(Name, false,
                hadFailure ? (continueOnError ? "partial" : "failed") : partial || errors.Count > 0 ? "partial" : "succeeded",
                steps, createdIds, modifiedIds, warnings, rollback);
            final["sheets"] = reports;
            return CommandResult.Ok(final);
        }

        private static bool TryParse(JObject item, int index, out Spec spec, out string error)
        {
            spec = null;
            error = null;
            var at = "sheets[" + index + "]";
            var unknown = item.Properties().Select(x => x.Name).FirstOrDefault(n => !SheetKeys.Contains(n));
            if (unknown != null) { error = at + " has unknown field '" + unknown + "'; valid: " + string.Join(", ", SheetKeys) + "."; return false; }
            var number = item.Value<string>("sheet_number");
            var name = item.Value<string>("sheet_name");
            if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(name)) { error = at + " needs sheet_number and sheet_name."; return false; }
            if (item["title_block_type_id"] != null && item["title_block_type_id"].Type != JTokenType.Integer) { error = at + ".title_block_type_id must be an integer."; return false; }
            if (item["title_block_type_id"] != null && item["title_block_name"] != null) { error = at + ": pass title_block_type_id or title_block_name, not both."; return false; }
            if (!TryIds(item, "view_ids", at, out var views, out error) || !TryIds(item, "schedule_ids", at, out var schedules, out error)) return false;
            if (views.Length + schedules.Length > MaxItemsPerSheet) { error = at + " places more than " + MaxItemsPerSheet + " views and schedules."; return false; }
            if (item["parameters"] != null && item["parameters"].Type != JTokenType.Object) { error = at + ".parameters must be an object {name: value}."; return false; }
            spec = new Spec
            {
                Number = number.Trim(),
                Name = name.Trim(),
                TitleBlockId = item.Value<long?>("title_block_type_id"),
                TitleBlockName = item.Value<string>("title_block_name"),
                ViewIds = views,
                ScheduleIds = schedules,
                Parameters = item["parameters"] as JObject ?? new JObject()
            };
            return true;
        }

        private static bool TryIds(JObject item, string field, string at, out long[] ids, out string error)
        {
            error = null;
            ids = new long[0];
            var token = item[field];
            if (token == null || token.Type == JTokenType.Null) return true;
            if (!(token is JArray array) || array.Any(t => t.Type != JTokenType.Integer))
            {
                error = at + "." + field + " must be an array of integers.";
                return false;
            }
            ids = array.Select(t => t.Value<long>()).ToArray();
            return true;
        }

        /// <summary>Errors start "Sheet NUMBER: ..." so the failing sheet can be skipped with continue_on_error.</summary>
        private static List<string> Validate(Document doc, List<Spec> specs)
        {
            var errors = new List<string>();
            foreach (var dup in specs.GroupBy(s => s.Number, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                errors.Add("Sheet " + dup.Key + ": the number is requested more than once.");
            var existing = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(s => s.SheetNumber),
                StringComparer.OrdinalIgnoreCase);
            var placed = new Dictionary<long, string>();
            foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
                placed[RevitCompat.GetId(vp.ViewId)] = (doc.GetElement(vp.SheetId) as ViewSheet)?.SheetNumber;
            var requested = new Dictionary<long, string>();

            foreach (var spec in specs)
            {
                var prefix = "Sheet " + spec.Number + ": ";
                if (existing.Contains(spec.Number)) errors.Add(prefix + "the sheet number already exists.");
                if (spec.TitleBlockId.HasValue || !string.IsNullOrEmpty(spec.TitleBlockName))
                {
                    if (!Lookup.TryTitleBlock(doc, spec.TitleBlockId, spec.TitleBlockName, out _, out var tbFailure))
                        errors.Add(prefix + tbFailure.Error);
                }
                foreach (var id in spec.ViewIds)
                {
                    var view = doc.GetElement(RevitCompat.ToElementId(id)) as View;
                    if (view == null || view.IsTemplate || view is ViewSheet || view is ViewSchedule)
                        errors.Add(prefix + "view_id " + id + " is not a placeable view (schedules go in schedule_ids).");
                    else if (placed.TryGetValue(id, out var onSheet) && view.ViewType != ViewType.Legend)
                        errors.Add(prefix + "view " + id + " ('" + view.Name + "') is already on sheet " + onSheet + ".");
                    else if (view.ViewType != ViewType.Legend && requested.TryGetValue(id, out var other))
                        errors.Add(prefix + "view " + id + " is also requested for sheet " + other + "; a view can be on one sheet only.");
                    else requested[id] = spec.Number;
                }
                foreach (var id in spec.ScheduleIds)
                {
                    var schedule = doc.GetElement(RevitCompat.ToElementId(id)) as ViewSchedule;
                    if (schedule == null || schedule.IsTemplate || schedule.IsTitleblockRevisionSchedule)
                        errors.Add(prefix + "schedule_id " + id + " is not a placeable schedule.");
                }
            }
            return errors;
        }

        /// <summary>Creates one sheet; returns null on success or the error. Sub-commands open their own transactions.</summary>
        private static string CreateOne(UIApplication app, Document doc, Spec spec, List<long> createdIds, List<long> modifiedIds,
            out JObject report, ref bool partial)
        {
            report = new JObject { ["sheet_number"] = spec.Number, ["sheet_name"] = spec.Name, ["created"] = false };
            var create = new JObject { ["sheet_number"] = spec.Number, ["sheet_name"] = spec.Name };
            if (spec.TitleBlockId.HasValue) create["title_block_type_id"] = spec.TitleBlockId.Value;
            if (!string.IsNullOrEmpty(spec.TitleBlockName)) create["title_block_name"] = spec.TitleBlockName;
            var created = WorkflowsHelpers.Invoke(new CreateSheetHandler(), app, create);
            if (!created.Success) return created.Error;

            var sheetId = created.Data.Value<long>("sheet_id");
            createdIds.Add(sheetId);
            report["sheet_id"] = sheetId;
            report["created"] = true;
            report["title_block_name"] = created.Data["title_block_name"];
            var sheet = (ViewSheet)doc.GetElement(RevitCompat.ToElementId(sheetId));

            var cells = WorkflowsHelpers.GridCells(doc, sheet, spec.ViewIds.Length + spec.ScheduleIds.Length, out var usedFrame);
            if (!usedFrame && cells.Count > 0) report["layout_warning"] = "The sheet has no title block; items were laid out on an A1 area at the sheet origin.";

            var viewports = new JArray();
            for (var i = 0; i < spec.ViewIds.Length; i++)
            {
                var placed = WorkflowsHelpers.Invoke(new PlaceViewOnSheetHandler(), app, new JObject
                {
                    ["view_id"] = spec.ViewIds[i],
                    ["sheet_id"] = sheetId,
                    ["x_mm"] = Mm.FromFeet(cells[i].center.X),
                    ["y_mm"] = Mm.FromFeet(cells[i].center.Y)
                });
                if (!placed.Success) return "view " + spec.ViewIds[i] + ": " + placed.Error;
                createdIds.Add(placed.Data.Value<long>("viewport_id"));
                viewports.Add(placed.Data);
            }
            report["viewports"] = viewports;

            var schedules = new JArray();
            for (var i = 0; i < spec.ScheduleIds.Length; i++)
            {
                var at = cells[spec.ViewIds.Length + i].topLeft;
                var placed = WorkflowsHelpers.Invoke(new PlaceScheduleOnSheetHandler(), app, new JObject
                {
                    ["schedule_id"] = spec.ScheduleIds[i],
                    ["sheet_id"] = sheetId,
                    ["x_mm"] = Mm.FromFeet(at.X),
                    ["y_mm"] = Mm.FromFeet(at.Y)
                });
                if (!placed.Success) return "schedule " + spec.ScheduleIds[i] + ": " + placed.Error;
                createdIds.Add(placed.Data.Value<long>("schedule_instance_id"));
                schedules.Add(placed.Data);
            }
            report["schedules"] = schedules;

            var updates = new JArray();
            if (spec.Parameters.Count > 0)
            {
                using (var tx = new Transaction(doc, "B2M: Set sheet parameters"))
                {
                    tx.Start();
                    try
                    {
                        foreach (var property in spec.Parameters.Properties())
                        {
                            var value = WorkflowsHelpers.AsComparableString(property.Value);
                            var update = new JObject { ["parameter"] = property.Name, ["value"] = value };
                            var parameter = sheet.LookupParameter(property.Name);
                            if (parameter == null)
                            {
                                update["status"] = "failed";
                                update["error"] = "no parameter named '" + property.Name + "' on the sheet.";
                            }
                            else if (WorkflowsHelpers.TrySetParameterFromString(parameter, value, out var setError))
                            {
                                update["status"] = "succeeded";
                                if (!modifiedIds.Contains(sheetId)) modifiedIds.Add(sheetId);
                            }
                            else
                            {
                                update["status"] = "failed";
                                update["error"] = setError;
                            }
                            if (update.Value<string>("status") == "failed") partial = true;
                            updates.Add(update);
                        }
                        if (tx.Commit() != TransactionStatus.Committed) return "Revit rolled the sheet parameter changes back; see failures.";
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                        return "setting parameters failed: " + ex.Message;
                    }
                }
            }
            report["parameter_updates"] = updates;
            return null;
        }
    }
}
