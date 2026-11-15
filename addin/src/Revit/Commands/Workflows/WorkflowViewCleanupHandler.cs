// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Workflows/WorkflowViewCleanupHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: the candidate caps count candidates (rvt-mcp's unused-view loop counted before filtering, so
// the truncation flag was unreliable); views that are dependent views, have dependents, or are open in Revit
// (rvt-mcp: only the active view) are protected; title block revision schedules are skipped;
// the deleted-id list is capped at 1000 with a total (doc.Delete also returns dependent elements); the delete
// transaction is "B2M: ..." and any Revit warning rolls it back (FailureScope).

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Workflows
{
    using View = Autodesk.Revit.DB.View;

    public sealed class WorkflowViewCleanupHandler : IRevitCommand
    {
        private const int MaxDeletedListed = 1000;

        public string Name => "workflow_view_cleanup";
        public string Description =>
            "Find printable views not on any sheet, empty schedules not on any sheet, and view-name outliers (limit default 200, at most 1000 per list). "
            + "With delete_empty_views true and dry_run false, deletes the candidates marked safe_to_delete. dry_run defaults to true.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""include_unused_views"":{""type"":""boolean""},
            ""include_empty_schedules"":{""type"":""boolean""},
            ""include_naming_outliers"":{""type"":""boolean""},
            ""delete_empty_views"":{""type"":""boolean""},
            ""dry_run"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var uiDoc = app.ActiveUIDocument;
            var doc = uiDoc?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var includeUnused = p.Value<bool?>("include_unused_views") ?? true;
            var includeEmpty = p.Value<bool?>("include_empty_schedules") ?? true;
            var includeNaming = p.Value<bool?>("include_naming_outliers") ?? true;
            var delete = p.Value<bool?>("delete_empty_views") ?? false;
            var dryRun = p.Value<bool?>("dry_run") ?? true;
            var limit = p.Value<int?>("limit") ?? 200;
            if (!includeUnused && !includeEmpty && !includeNaming) return CommandResult.Fail("INVALID_PARAMS: at least one include_* option must be true.");
            if (limit < 1 || limit > 1000) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to 1000.");
            if (!dryRun && !delete) return CommandResult.Fail("INVALID_PARAMS: dry_run false only makes sense with delete_empty_views true.");

            var openViews = new HashSet<long>();
            try
            {
                foreach (var ui in uiDoc.GetOpenUIViews()) openViews.Add(RevitCompat.GetId(ui.ViewId));
            }
            catch
            {
                // Without the open-view list only the active view is protected.
            }
            if (doc.ActiveView != null) openViews.Add(RevitCompat.GetId(doc.ActiveView.Id));

            var placed = new HashSet<long>();
            foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()) placed.Add(RevitCompat.GetId(vp.ViewId));
            foreach (var ssi in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
                placed.Add(RevitCompat.GetId(ssi.ScheduleId));

            var steps = new JArray();
            var warnings = new List<string>();
            var candidates = new JObject();
            var proposed = new JArray();

            if (includeUnused)
            {
                var list = UnusedViews(doc, placed, openViews, limit, out var truncated);
                candidates["unused_views"] = list;
                if (truncated) warnings.Add("Unused view candidates were cut at " + limit + ".");
                foreach (var item in list.OfType<JObject>().Where(i => i.Value<bool>("safe_to_delete"))) proposed.Add(item.DeepClone());
                steps.Add(WorkflowsHelpers.Step("Find Unused Views", "view collectors", "succeeded", "Printable views not on any sheet.", new { candidates = list.Count, truncated }));
            }
            if (includeEmpty)
            {
                var list = EmptySchedules(doc, placed, openViews, limit, out var truncated);
                candidates["empty_schedules"] = list;
                if (truncated) warnings.Add("Empty schedule candidates were cut at " + limit + ".");
                foreach (var item in list.OfType<JObject>().Where(i => i.Value<bool>("safe_to_delete"))) proposed.Add(item.DeepClone());
                steps.Add(WorkflowsHelpers.Step("Find Empty Schedules", "ViewSchedule.GetTableData", "succeeded", "Schedules without body rows.", new { candidates = list.Count, truncated }));
            }
            if (includeNaming)
            {
                var list = NamingOutliers(doc, limit, out var truncated);
                candidates["naming_outliers"] = list;
                if (truncated) warnings.Add("Naming outliers were cut at " + limit + ".");
                steps.Add(WorkflowsHelpers.Step("Find Naming Outliers", "name checks", "succeeded",
                    "View names with extra whitespace, forbidden characters, or duplicates. workflow_naming_normalization can fix them.", new { candidates = list.Count, truncated }));
            }

            var deleted = new List<long>();
            if (!delete || dryRun || proposed.Count == 0)
            {
                if (delete && dryRun)
                    steps.Add(WorkflowsHelpers.Step("Delete Cleanup Candidates", "doc.Delete", "skipped", "Dry run: nothing deleted.", new { proposed_deletes = proposed.Count }));
                return CommandResult.Ok(Result(dryRun, "succeeded", steps, warnings, candidates, proposed, deleted,
                    WorkflowsHelpers.Rollback("Transaction", false, dryRun ? "Dry run; no transaction opened." : "No safe candidates to delete.")));
            }

            using (var tx = new Transaction(doc, "B2M: Workflow view cleanup"))
            {
                tx.Start();
                try
                {
                    var ids = proposed.OfType<JObject>().Select(i => RevitCompat.ToElementId(i.Value<long>("element_id"))).ToList();
                    foreach (var id in doc.Delete(ids)) deleted.Add(RevitCompat.GetId(id));
                    if (tx.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit rolled the delete back; see failures.");
                    steps.Add(WorkflowsHelpers.Step("Delete Cleanup Candidates", "doc.Delete", "succeeded", "Deleted the safe candidates.",
                        new { requested = ids.Count, deleted_elements = deleted.Count }));
                    return CommandResult.Ok(Result(false, "succeeded", steps, warnings, candidates, proposed, deleted,
                        WorkflowsHelpers.Rollback("Transaction", false, "Delete committed.")));
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    steps.Add(WorkflowsHelpers.Step("Delete Cleanup Candidates", "doc.Delete", "failed", "Delete the safe candidates.", null, ex.Message));
                    return CommandResult.Ok(Result(false, "failed", steps, warnings, candidates, proposed, new List<long>(),
                        WorkflowsHelpers.Rollback("Transaction", true, "The delete failed and was rolled back; nothing was deleted.")));
                }
            }
        }

        private JObject Result(bool dryRun, string status, JArray steps, IEnumerable<string> warnings, JObject candidates, JArray proposed,
            List<long> deleted, JObject rollback)
        {
            var result = WorkflowsHelpers.Envelope(Name, dryRun, status, steps, new long[0], new long[0], warnings, rollback);
            result["candidates"] = candidates;
            result["proposed_deletes"] = proposed;
            result["deleted_count"] = deleted.Count;
            result["deleted_ids"] = new JArray(deleted.Take(MaxDeletedListed));
            result["deleted_ids_truncated"] = deleted.Count > MaxDeletedListed;
            return result;
        }

        private static bool CanBePrinted(View view)
        {
            try { return view.CanBePrinted; }
            catch { return false; }
        }

        private static JArray UnusedViews(Document doc, HashSet<long> placed, HashSet<long> open, int limit, out bool truncated)
        {
            truncated = false;
            var list = new JArray();
            foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (view.IsTemplate || view is ViewSheet || view is ViewSchedule || view.ViewType == ViewType.ColumnSchedule || !CanBePrinted(view)) continue;
                var id = RevitCompat.GetId(view.Id);
                if (placed.Contains(id)) continue;
                if (list.Count >= limit)
                {
                    truncated = true;
                    break;
                }
                string protection = null;
                if (open.Contains(id)) protection = "The view is open in Revit.";
                else
                {
                    try
                    {
                        if (view.GetDependentViewIds().Count > 0) protection = "The view has dependent views.";
                        else if (view.GetPrimaryViewId() != ElementId.InvalidElementId) protection = "The view is a dependent view.";
                    }
                    catch
                    {
                        protection = "The view's dependencies could not be read.";
                    }
                }
                list.Add(new JObject
                {
                    ["element_id"] = id,
                    ["name"] = view.Name,
                    ["view_type"] = view.ViewType.ToString(),
                    ["safe_to_delete"] = protection == null,
                    ["protection_reason"] = protection
                });
            }
            return list;
        }

        private static JArray EmptySchedules(Document doc, HashSet<long> placed, HashSet<long> open, int limit, out bool truncated)
        {
            truncated = false;
            var list = new JArray();
            foreach (var schedule in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (schedule.IsTemplate || schedule.IsTitleblockRevisionSchedule || !WorkflowModelAuditHandler.IsScheduleEmpty(schedule)) continue;
                if (list.Count >= limit)
                {
                    truncated = true;
                    break;
                }
                var id = RevitCompat.GetId(schedule.Id);
                var protection = placed.Contains(id) ? "The schedule is placed on a sheet." : open.Contains(id) ? "The schedule is open in Revit." : null;
                list.Add(new JObject
                {
                    ["element_id"] = id,
                    ["name"] = schedule.Name,
                    ["is_placed"] = placed.Contains(id),
                    ["safe_to_delete"] = protection == null,
                    ["protection_reason"] = protection
                });
            }
            return list;
        }

        private static JArray NamingOutliers(Document doc, int limit, out bool truncated)
        {
            truncated = false;
            var list = new JArray();
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
            var duplicates = new HashSet<string>(views.GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1).Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var view in views.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            {
                var normalized = WorkflowsHelpers.NormalizeName(view.Name);
                var reasons = new JArray();
                if (string.IsNullOrWhiteSpace(view.Name)) reasons.Add("blank_name");
                else if (!string.Equals(normalized, view.Name, StringComparison.Ordinal)) reasons.Add("normalized_name_differs");
                if (duplicates.Contains(view.Name ?? "")) reasons.Add("duplicate_name");
                if (reasons.Count == 0) continue;
                if (list.Count >= limit)
                {
                    truncated = true;
                    break;
                }
                list.Add(new JObject
                {
                    ["element_id"] = RevitCompat.GetId(view.Id),
                    ["name"] = view.Name,
                    ["view_type"] = view.ViewType.ToString(),
                    ["suggested"] = normalized,
                    ["reasons"] = reasons
                });
            }
            return list;
        }
    }
}
