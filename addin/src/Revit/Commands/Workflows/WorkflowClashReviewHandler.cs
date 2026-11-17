// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Workflows/WorkflowClashReviewHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: the clash check runs locally (WorkflowsHelpers.DetectClashes, a compact copy of
// clash_detection) instead of calling the ClashDetectionHandler; max_pairs now caps the bounding-box candidate
// pairs that get the solid test (rvt-mcp counted every A x B pair, so a modest model hit the cap before
// overlapping pairs were reached) and each side is capped at 5000 elements; only solid-confirmed clashes are
// reported; categories accept BuiltInCategory names and a miss lists the valid ones; view_id must be a view
// (not a template); transactions are named "Plinth: ..."; the review view is named "Plinth Clash Review"; any Revit
// warning rolls the whole group back (FailureScope).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Workflows
{
    using View = Autodesk.Revit.DB.View;

    public sealed class WorkflowClashReviewHandler : IRevitCommand
    {
        private const int MaxElementsPerSide = 5000;
        private const int MaxResults = 500;

        public string Name => "workflow_clash_review";
        public string Description =>
            "Find solid clashes between two categories (BuiltInCategory names such as OST_Walls, OST_DuctCurves), then optionally create a "
            + "3D review view, colour the clashing elements and add text markers. dry_run (default true) only reports. "
            + "max_pairs (default 200, at most 2000) caps the overlapping pairs that get the solid test.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""category_a"":{""type"":""string""},
            ""category_b"":{""type"":""string""},
            ""view_id"":{""type"":""integer""},
            ""max_pairs"":{""type"":""integer""},
            ""create_review_view"":{""type"":""boolean""},
            ""color_hits"":{""type"":""boolean""},
            ""create_markers"":{""type"":""boolean""},
            ""dry_run"":{""type"":""boolean""},
            ""continue_on_error"":{""type"":""boolean""}},
            ""required"":[""category_a"",""category_b""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var maxPairs = p.Value<int?>("max_pairs") ?? 200;
            var createReviewView = p.Value<bool?>("create_review_view") ?? true;
            var colorHits = p.Value<bool?>("color_hits") ?? true;
            var createMarkers = p.Value<bool?>("create_markers") ?? false;
            var dryRun = p.Value<bool?>("dry_run") ?? true;
            var continueOnError = p.Value<bool?>("continue_on_error") ?? false;
            if (maxPairs < 1 || maxPairs > 2000) return CommandResult.Fail("INVALID_PARAMS: max_pairs must be 1 to 2000.");

            if (!WorkflowsHelpers.TryCategory(doc, p.Value<string>("category_a"), out var categoryA, out var failure)) return failure;
            if (!WorkflowsHelpers.TryCategory(doc, p.Value<string>("category_b"), out var categoryB, out failure)) return failure;
            View sourceView = null;
            var viewId = p.Value<long?>("view_id");
            if (viewId.HasValue && !Lookup.TryView(doc, viewId.Value, out sourceView, out failure)) return failure;

            var steps = new JArray();
            var warnings = new List<string>();
            var createdIds = new List<long>();
            var modifiedIds = new List<long>();

            var clashData = WorkflowsHelpers.DetectClashes(doc, categoryA, categoryB, sourceView, MaxElementsPerSide, maxPairs, Math.Min(maxPairs, MaxResults));
            var clashes = (JArray)clashData["clashes"];
            steps.Add(WorkflowsHelpers.Step("Clash Detection", "clash_detection (local)", "succeeded",
                "Detect solid clashes between '" + categoryA.Name + "' and '" + categoryB.Name + "'.",
                new JObject
                {
                    ["returned"] = clashes.Count,
                    ["scanned_a"] = clashData["scanned_a"],
                    ["scanned_b"] = clashData["scanned_b"],
                    ["bbox_candidates_tested"] = clashData["bbox_candidates_tested"],
                    ["truncated"] = clashData["truncated"],
                    ["truncation_reason"] = clashData["truncation_reason"],
                    ["failed"] = clashData["failed"]
                }));
            if (clashData.Value<bool>("truncated"))
                warnings.Add("Clash detection stopped early (" + clashData.Value<string>("truncation_reason") + "); more clashes may exist.");

            if (clashes.Count == 0)
            {
                warnings.Add("No clashes were found; write steps were skipped.");
                return CommandResult.Ok(Result(dryRun, "succeeded", steps, createdIds, modifiedIds, warnings,
                    WorkflowsHelpers.Rollback("None", false, "No clashes found."), clashes, null, new JArray()));
            }

            var hitIds = WorkflowsHelpers.ClashElementIds(clashes);
            if (dryRun)
            {
                if (createReviewView)
                    steps.Add(WorkflowsHelpers.Step("Create Review View", "View3D.CreateIsometric", "skipped", "Dry run: a 3D review view would be created.", new { planned = true }));
                if (colorHits)
                    steps.Add(WorkflowsHelpers.Step("Color Clash Hits", "View.SetElementOverrides", "skipped", "Dry run: clashing elements would be coloured.", new { element_count = hitIds.Count }));
                if (createMarkers)
                    steps.Add(WorkflowsHelpers.Step("Create Clash Markers", "TextNote.Create", "skipped", "Dry run: markers would be created where the target view can host text.", new { planned = clashes.Count }));
                return CommandResult.Ok(Result(true, "succeeded", steps, createdIds, modifiedIds, warnings,
                    WorkflowsHelpers.Rollback("TransactionGroup", false, "Dry run; nothing was written."), clashes, null, new JArray()));
            }

            View targetView = null;
            var markerIds = new JArray();
            var hadError = false;
            using (var group = new TransactionGroup(doc, "Plinth: Workflow clash review"))
            {
                group.Start();
                using (var tx = new Transaction(doc, "Plinth: Clash review"))
                {
                    tx.Start();
                    try
                    {
                        if (createReviewView)
                        {
                            try
                            {
                                targetView = CreateReviewView(doc);
                                createdIds.Add(RevitCompat.GetId(targetView.Id));
                                steps.Add(WorkflowsHelpers.Step("Create Review View", "View3D.CreateIsometric", "succeeded", "Create a 3D clash review view.",
                                    new { view_id = RevitCompat.GetId(targetView.Id), name = targetView.Name }));
                            }
                            catch (Exception ex)
                            {
                                hadError = true;
                                steps.Add(WorkflowsHelpers.Step("Create Review View", "View3D.CreateIsometric", "failed", "Create a 3D clash review view.", null, ex.Message));
                                if (!continueOnError) throw;
                            }
                        }
                        if (targetView == null) targetView = ResolveGraphicsView(doc, sourceView);

                        if (colorHits)
                        {
                            try
                            {
                                var result = ApplyOverrides(doc, targetView, hitIds, modifiedIds);
                                steps.Add(WorkflowsHelpers.Step("Color Clash Hits", "View.SetElementOverrides", "succeeded",
                                    "Colour clashing elements red/orange in the target view.", result));
                            }
                            catch (Exception ex)
                            {
                                hadError = true;
                                steps.Add(WorkflowsHelpers.Step("Color Clash Hits", "View.SetElementOverrides", "failed", "Colour clashing elements in the target view.", null, ex.Message));
                                if (!continueOnError) throw;
                            }
                        }

                        if (createMarkers)
                        {
                            try
                            {
                                var result = CreateMarkers(doc, targetView, clashes, markerIds);
                                foreach (var id in markerIds) createdIds.Add(id.Value<long>());
                                steps.Add(WorkflowsHelpers.Step("Create Clash Markers", "TextNote.Create",
                                    result.Value<int>("created") > 0 ? "succeeded" : "skipped", "Create text-note clash markers in a view that can host text.", result));
                            }
                            catch (Exception ex)
                            {
                                hadError = true;
                                steps.Add(WorkflowsHelpers.Step("Create Clash Markers", "TextNote.Create", "failed", "Create text-note clash markers.", null, ex.Message));
                                if (!continueOnError) throw;
                            }
                        }

                        if (tx.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Revit rolled the clash review transaction back; see failures.");
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                        group.RollBack();
                        warnings.Add(ex.Message);
                        return CommandResult.Ok(Result(false, "failed", steps, new long[0], new long[0], warnings,
                            WorkflowsHelpers.Rollback("TransactionGroup", true, "A write step failed (continue_on_error=false) or Revit rolled back; nothing was changed."),
                            clashes, null, new JArray()));
                    }
                }
                if (group.Assimilate() != TransactionStatus.Committed)
                    return CommandResult.Fail("NOT_COMMITTED: Revit did not commit the clash review; nothing was changed.");
            }

            return CommandResult.Ok(Result(false, hadError ? "partial" : "succeeded", steps, createdIds, modifiedIds, warnings,
                WorkflowsHelpers.Rollback("TransactionGroup", false, "Committed the clash review write steps."), clashes, targetView, markerIds));
        }

        private JObject Result(bool dryRun, string status, JArray steps, IEnumerable<long> created, IEnumerable<long> modified,
            IEnumerable<string> warnings, JObject rollback, JArray clashes, View reviewView, JArray markerIds)
        {
            var result = WorkflowsHelpers.Envelope(Name, dryRun, status, steps, created, modified, warnings, rollback);
            result["clashes"] = clashes ?? new JArray();
            result["review_view_id"] = reviewView == null ? JValue.CreateNull() : new JValue(RevitCompat.GetId(reviewView.Id));
            result["marker_ids"] = markerIds ?? new JArray();
            return result;
        }

        private static View3D CreateReviewView(Document doc)
        {
            var type = doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.ViewType3D)) as ViewFamilyType
                ?? new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
            if (type == null || type.ViewFamily != ViewFamily.ThreeDimensional)
                throw new InvalidOperationException("the project has no 3D view type.");
            var view = View3D.CreateIsometric(doc, type.Id);
            view.Name = WorkflowsHelpers.UniqueName(WorkflowsHelpers.ViewNames(doc), "Plinth Clash Review");
            return view;
        }

        private static View ResolveGraphicsView(Document doc, View preferred)
        {
            if (AllowsOverrides(preferred)) return preferred;
            if (preferred != null)
                throw new InvalidOperationException("view '" + preferred.Name + "' does not allow graphics overrides; enable create_review_view.");
            if (AllowsOverrides(doc.ActiveView)) return doc.ActiveView;
            throw new InvalidOperationException("the active view does not allow graphics overrides; pass view_id or enable create_review_view.");
        }

        private static bool AllowsOverrides(View view)
        {
            try { return view != null && !view.IsTemplate && view.AreGraphicsOverridesAllowed(); }
            catch { return false; }
        }

        private static JObject ApplyOverrides(Document doc, View view, List<long> elementIds, List<long> modifiedIds)
        {
            var red = new Color(220, 40, 40);
            var orange = new Color(255, 145, 0);
            var solidFill = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(f =>
                {
                    try { return f.GetFillPattern().IsSolidFill; }
                    catch { return false; }
                });
            var failed = new JArray();
            var index = 0;
            foreach (var rawId in elementIds)
            {
                try
                {
                    var id = RevitCompat.ToElementId(rawId);
                    if (doc.GetElement(id) == null)
                    {
                        failed.Add(new JObject { ["element_id"] = rawId, ["error"] = "element not found." });
                        continue;
                    }
                    var color = index++ % 2 == 0 ? red : orange;
                    var settings = view.GetElementOverrides(id) ?? new OverrideGraphicSettings();
                    settings.SetProjectionLineColor(color);
                    settings.SetSurfaceForegroundPatternColor(color);
                    settings.SetSurfaceTransparency(35);
                    if (solidFill != null)
                    {
                        settings.SetSurfaceForegroundPatternId(solidFill.Id);
                        settings.SetSurfaceForegroundPatternVisible(true);
                    }
                    view.SetElementOverrides(id, settings);
                    modifiedIds.Add(rawId);
                }
                catch (Exception ex)
                {
                    failed.Add(new JObject { ["element_id"] = rawId, ["error"] = ex.Message });
                }
            }
            return new JObject
            {
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["view_name"] = view.Name,
                ["requested"] = elementIds.Count,
                ["modified"] = elementIds.Count - failed.Count,
                ["failed"] = failed
            };
        }

        private static JObject CreateMarkers(Document doc, View view, JArray clashes, JArray markerIds)
        {
            if (!CanHostText(view))
                return new JObject
                {
                    ["created"] = 0,
                    ["skipped"] = clashes.Count,
                    ["reason"] = "view '" + (view?.Name ?? "?") + "' cannot host text notes (3D views cannot); pass a plan, section or elevation as view_id with create_review_view false."
                };
            var textType = doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType)) as TextNoteType
                ?? new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().FirstOrDefault();
            if (textType == null) throw new InvalidOperationException("the project has no text note type.");

            var created = 0;
            foreach (var clash in clashes.OfType<JObject>())
            {
                var center = WorkflowsHelpers.ClashCenter(clash);
                if (center == null) continue;
                var note = TextNote.Create(doc, view.Id, center, "CLASH " + (created + 1).ToString(CultureInfo.InvariantCulture), textType.Id);
                markerIds.Add(RevitCompat.GetId(note.Id));
                created++;
            }
            return new JObject { ["created"] = created, ["skipped"] = clashes.Count - created, ["view_id"] = RevitCompat.GetId(view.Id) };
        }

        private static bool CanHostText(View view)
        {
            if (view == null || view.IsTemplate) return false;
            switch (view.ViewType)
            {
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.EngineeringPlan:
                case ViewType.AreaPlan:
                case ViewType.Section:
                case ViewType.Elevation:
                case ViewType.Detail:
                case ViewType.DraftingView:
                    return true;
                default:
                    return false;
            }
        }
    }
}
