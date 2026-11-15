// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Sheets/AlignViewportsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: behaviour kept (modes, dry_run default true, pinned/saved-position refusal, read-back
// verification, partial-apply reporting); params arrive parsed and schema-checked, so the camelCase
// aliases and hand-parsing of viewport_ids are gone; errors are "CODE: message"; sheet lookup lists the
// valid sheets on a miss; feet/mm conversion goes through Mm.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Sheets
{
    public sealed class AlignViewportsHandler : IRevitCommand
    {
        public string Name => "align_viewports";
        public string Description =>
            "Align viewports to a reference viewport, or centre them on the sheet's title block, using sheet-space millimetres of the visible crop rectangle. "
            + "dry_run defaults to true (plan only); pass dry_run: false to move.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""sheet_id"":{""type"":""integer""},
            ""sheet_number"":{""type"":""string""},
            ""mode"":{""type"":""string"",""enum"":[""left"",""right"",""top"",""bottom"",""center_x"",""center_y"",""center"",""center_on_sheet""]},
            ""reference_viewport_id"":{""type"":""integer""},
            ""viewport_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""dry_run"":{""type"":""boolean""}},
            ""required"":[""mode""]}";
        // dry_run is a read, but the same command moves viewports, so it is a write command.
        public bool ReadOnly => false;

        // Moves smaller than 0.0005 mm are treated as already aligned.
        private static readonly double MoveToleranceFeet = Mm.ToFeet(0.0005);

        // After applying, every rectangle must land within 0.01 mm of the plan.
        private const double VerifyToleranceMm = 0.01;

        private sealed class Move
        {
            public ViewportSnapshot Before;
            public double Dx;
            public double Dy;
            public bool Changed;
        }

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var modeText = p.Value<string>("mode");
            if (!ViewportLayoutMath.TryParseMode(modeText, out var mode))
                return CommandResult.Fail("INVALID_PARAMS: mode must be one of left, right, top, bottom, center_x, center_y, center, center_on_sheet.");

            var dryRun = p.Value<bool?>("dry_run") ?? true;

            if (!Lookup.TrySheet(doc, p.Value<long?>("sheet_id"), p.Value<string>("sheet_number"), out var sheet, out var failure))
                return failure;

            var viewports = ViewportSheetSupport.GetViewports(doc, sheet);
            var byId = viewports.ToDictionary(vp => RevitCompat.GetId(vp.Id));

            // Reference viewport
            ViewportSnapshot reference = null;
            var referenceId = p.Value<long?>("reference_viewport_id");
            if (ViewportLayoutMath.NeedsReference(mode))
            {
                if (!referenceId.HasValue)
                    return CommandResult.Fail("INVALID_PARAMS: reference_viewport_id is required for mode '" + modeText + "'.");
                if (!byId.TryGetValue(referenceId.Value, out var referenceViewport))
                    return CommandResult.Fail("VIEWPORT_NOT_ON_SHEET: reference_viewport_id " + referenceId.Value + " is not a viewport on sheet " + sheet.SheetNumber + ".",
                        new { valid_viewport_ids = byId.Keys.OrderBy(id => id) });

                reference = ViewportSheetSupport.Read(doc, referenceViewport);
                if (!reference.Used.HasValue)
                    return CommandResult.Fail("NO_USABLE_RECT: the reference viewport has no usable rectangle: " + string.Join(", ", reference.Warnings) + ".");
            }
            else if (referenceId.HasValue)
            {
                return CommandResult.Fail("INVALID_PARAMS: reference_viewport_id is not used by mode 'center_on_sheet'.");
            }

            // Targets
            List<Viewport> targets;
            if (p["viewport_ids"] is JArray idArray)
            {
                if (idArray.Count == 0)
                    return CommandResult.Fail("INVALID_PARAMS: viewport_ids must be a non-empty array of viewport ids when supplied.");
                var ids = idArray.Select(t => t.Value<long>()).ToList();
                var unknown = ids.Where(id => !byId.ContainsKey(id)).Distinct().OrderBy(id => id).ToArray();
                if (unknown.Length > 0)
                    return CommandResult.Fail("VIEWPORT_NOT_ON_SHEET: viewport id(s) not on sheet " + sheet.SheetNumber + ": " + string.Join(", ", unknown) + ".",
                        new { valid_viewport_ids = byId.Keys.OrderBy(id => id) });

                targets = ids.Distinct().Where(id => id != referenceId).Select(id => byId[id]).ToList();
            }
            else
            {
                targets = viewports.Where(vp => RevitCompat.GetId(vp.Id) != referenceId).ToList();
            }

            if (targets.Count == 0)
                return CommandResult.Fail("NOTHING_TO_ALIGN: no viewports to move. Select at least one viewport other than the reference.");

            var snapshots = targets.Select(vp => ViewportSheetSupport.Read(doc, vp)).ToList();

            var blocked = new List<string>();
            foreach (var s in snapshots)
            {
                if (s.Pinned)
                    blocked.Add(s.ViewportId + " (pinned)");
                else if (s.SavedPosition != null)
                    blocked.Add(s.ViewportId + " (bound to saved position '" + s.SavedPosition + "')");
                else if (!s.Used.HasValue)
                    blocked.Add(s.ViewportId + " (" + (s.Warnings.Count > 0 ? string.Join(", ", s.Warnings) : "no usable rectangle") + ")");
            }
            if (blocked.Count > 0)
                return CommandResult.Fail("VIEWPORT_BLOCKED: cannot move viewport(s): " + string.Join("; ", blocked)
                    + ". Unpin them, or clear their saved position in Revit (Revit puts a viewport back at its saved position), or leave them out of viewport_ids. Nothing was changed.");

            // Plan
            SheetRect? frame = null;
            var moves = new List<Move>();
            if (mode == ViewportAlignMode.CenterOnSheet)
            {
                frame = ViewportSheetSupport.GetSheetFrame(doc, sheet);
                if (!frame.HasValue)
                    return CommandResult.Fail("NO_TITLEBLOCK: sheet " + sheet.SheetNumber + " has no title block, so the sheet frame is unknown and the viewports cannot be centred on it.");

                var group = ViewportLayoutMath.Union(snapshots.Select(s => s.Used.Value));
                var delta = ViewportLayoutMath.CenterGroupOnFrame(frame.Value, group);
                foreach (var s in snapshots)
                    moves.Add(NewMove(s, delta.Dx, delta.Dy));
            }
            else
            {
                foreach (var s in snapshots)
                {
                    var delta = ViewportLayoutMath.AlignDelta(reference.Used.Value, s.Used.Value, mode);
                    moves.Add(NewMove(s, delta.Dx, delta.Dy));
                }
            }

            var changed = moves.Where(m => m.Changed).ToList();

            if (dryRun || changed.Count == 0)
            {
                return CommandResult.Ok(BuildResult(sheet, modeText, reference, frame, moves, null,
                    applied: false, dryRun: dryRun,
                    note: dryRun
                        ? "Nothing was changed. Call again with dry_run=false to apply these moves."
                        : "Nothing to move: the viewports already satisfy this alignment."));
            }

            using (var tx = new Transaction(doc, "B2M: Align Viewports"))
            {
                tx.Start();
                try
                {
                    foreach (var m in changed)
                    {
                        var center = m.Before.Viewport.GetBoxCenter();
                        m.Before.Viewport.SetBoxCenter(new XYZ(center.X + m.Dx, center.Y + m.Dy, center.Z));
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("MOVE_FAILED: could not move the viewports; nothing was changed: " + ex.Message);
                }
            }

            // Read back what Revit actually holds. A committed transaction does not prove the viewport
            // moved: Revit can put it back at regeneration, so each move is judged on the read-back.
            var after = new Dictionary<long, ViewportSnapshot>();
            var failed = new List<object>();
            var maxErrorMm = 0.0;
            foreach (var m in moves)
            {
                var read = ViewportSheetSupport.Read(doc, m.Before.Viewport);
                after[m.Before.ViewportId] = read;
                if (!m.Changed) continue;

                double? errorMm = null;
                if (read.Used.HasValue)
                {
                    var expected = m.Before.Used.Value.Translated(m.Dx, m.Dy);
                    errorMm = Mm.FromFeet(new[]
                    {
                        Math.Abs(read.Used.Value.MinX - expected.MinX), Math.Abs(read.Used.Value.MaxX - expected.MaxX),
                        Math.Abs(read.Used.Value.MinY - expected.MinY), Math.Abs(read.Used.Value.MaxY - expected.MaxY)
                    }.Max(), 6);
                    if (errorMm.Value > maxErrorMm) maxErrorMm = errorMm.Value;
                }

                if (!errorMm.HasValue || errorMm.Value > VerifyToleranceMm)
                    failed.Add(new
                    {
                        viewport_id = m.Before.ViewportId,
                        view_name = m.Before.ViewName,
                        position_error_mm = errorMm.HasValue ? (double?)Math.Round(errorMm.Value, 4) : null,
                        saved_position = read.SavedPosition
                    });
            }

            var plannedCount = changed.Count;
            var heldCount = plannedCount - failed.Count;
            var status = ViewportLayoutMath.ClassifyApply(plannedCount, heldCount);

            if (status == "not_applied")
                return CommandResult.Fail("NOT_APPLIED: Revit did not keep any of the " + plannedCount + " planned move(s); the viewports are where they were"
                    + " (largest position error " + Math.Round(maxErrorMm, 4) + " mm). Check the sheet for something that re-positions viewports,"
                    + " such as a saved position or an add-in.");

            if (status == "partially_applied")
                return CommandResult.Ok(BuildResult(sheet, modeText, reference, frame, moves, after,
                    applied: false, dryRun: false, status: status, heldCount: heldCount, failed: failed,
                    note: "Revit kept " + heldCount + " of " + plannedCount + " moves; failed_viewports lists the ones it put back."
                        + " The moves that held are one Revit Undo step. Check the sheet."));

            return CommandResult.Ok(BuildResult(sheet, modeText, reference, frame, moves, after,
                applied: true, dryRun: false, status: status, heldCount: heldCount, failed: failed,
                note: "Applied as one Revit Undo step. Read back max_position_error_mm = " + Math.Round(maxErrorMm, 4) + " (verified)."));
        }

        private static Move NewMove(ViewportSnapshot before, double dx, double dy)
        {
            return new Move
            {
                Before = before,
                Dx = dx,
                Dy = dy,
                Changed = Math.Abs(dx) > MoveToleranceFeet || Math.Abs(dy) > MoveToleranceFeet
            };
        }

        private static object BuildResult(
            ViewSheet sheet, string mode, ViewportSnapshot reference, SheetRect? frame,
            List<Move> moves, Dictionary<long, ViewportSnapshot> after,
            bool applied, bool dryRun, string note,
            string status = null, int heldCount = 0, List<object> failed = null)
        {
            var items = moves.Select(m =>
            {
                var plannedAfter = m.Before.Used.Value.Translated(m.Dx, m.Dy);
                SheetRect? rectAfter = plannedAfter;
                if (after != null && after.TryGetValue(m.Before.ViewportId, out var read) && read.Used.HasValue)
                    rectAfter = read.Used;

                return new
                {
                    viewport_id = m.Before.ViewportId,
                    view_name = m.Before.ViewName,
                    changed = m.Changed,
                    delta_sheet_mm = new { x = ViewportSheetSupport.ToMm(m.Dx), y = ViewportSheetSupport.ToMm(m.Dy) },
                    rect_before_sheet_mm = ViewportSheetSupport.RectDto(m.Before.Used),
                    rect_after_sheet_mm = ViewportSheetSupport.RectDto(rectAfter),
                    rect_source = m.Before.RectSource,
                    warnings = m.Before.Warnings
                };
            }).ToList();

            return new
            {
                dry_run = dryRun,
                applied,
                status = status ?? (dryRun ? "dry_run" : "nothing_to_move"),
                mode,
                sheet_id = RevitCompat.GetId(sheet.Id),
                sheet_number = sheet.SheetNumber,
                units = "mm",
                coordinate_frame = ViewportSheetSupport.CoordinateFrame,
                alignment_rect_note = ViewportSheetSupport.AlignmentRectNote,
                reference_viewport_id = reference?.ViewportId,
                reference_rect_sheet_mm = ViewportSheetSupport.RectDto(reference?.Used),
                frame_sheet_mm = ViewportSheetSupport.RectDto(frame),
                moved_count = heldCount,
                planned_move_count = moves.Count(m => m.Changed),
                unchanged_count = moves.Count(m => !m.Changed),
                failed_viewports = failed ?? new List<object>(),
                moves = items,
                note
            };
        }
    }
}
