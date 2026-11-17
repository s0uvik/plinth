// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Sheets/DuplicateSheetHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: one Transaction instead of a TransactionGroup wrapping one Transaction (same atomicity,
// and nests cleanly inside batch_execute's group); source sheet through Lookup.TrySheet (a miss lists the
// valid sheets; one of source_sheet_id / source_sheet_number is required, not both); the new title block is
// moved to the source title block's position, and viewports keep their viewport type and rotation (rvt-mcp
// left the title block at the origin and used the default viewport type); a sheet with several title blocks
// copies the first and warns; failures are errors; mm through Mm; the camelCase aliases are gone.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Sheets
{
    // Plinth.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class DuplicateSheetHandler : IRevitCommand
    {
        public string Name => "duplicate_sheet";
        public string Description =>
            "Duplicate a sheet (source_sheet_id or source_sheet_number) as new_sheet_number (new_sheet_name defaults to the source name): "
            + "same title block and its parameters, viewports at the same positions, schedules (include_schedules, default true) and "
            + "additional revisions (include_revisions, default true). A view that cannot sit on two sheets is duplicated with "
            + "duplicate_view_option (duplicate | with_detailing (default) | as_dependent); legends and other reusable views are placed again "
            + "unless reuse_views_when_allowed is false.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""source_sheet_id"":{""type"":""integer""},
            ""source_sheet_number"":{""type"":""string""},
            ""new_sheet_number"":{""type"":""string""},
            ""new_sheet_name"":{""type"":""string""},
            ""duplicate_view_option"":{""type"":""string"",""enum"":[""duplicate"",""with_detailing"",""as_dependent""]},
            ""include_schedules"":{""type"":""boolean""},
            ""include_revisions"":{""type"":""boolean""},
            ""reuse_views_when_allowed"":{""type"":""boolean""}},
            ""required"":[""new_sheet_number""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var sourceId = p.Value<long?>("source_sheet_id");
            var sourceNumber = p.Value<string>("source_sheet_number");
            if (sourceId.HasValue == !string.IsNullOrEmpty(sourceNumber))
                return CommandResult.Fail("INVALID_PARAMS: pass exactly one of source_sheet_id or source_sheet_number.");
            var newNumber = p.Value<string>("new_sheet_number");
            if (string.IsNullOrWhiteSpace(newNumber)) return CommandResult.Fail("INVALID_PARAMS: new_sheet_number cannot be empty.");
            var newName = p.Value<string>("new_sheet_name");
            var option = p.Value<string>("duplicate_view_option") ?? "with_detailing";
            var dupOption = option == "duplicate" ? ViewDuplicateOption.Duplicate
                : option == "as_dependent" ? ViewDuplicateOption.AsDependent : ViewDuplicateOption.WithDetailing;
            var includeSchedules = p.Value<bool?>("include_schedules") ?? true;
            var includeRevisions = p.Value<bool?>("include_revisions") ?? true;
            var reuse = p.Value<bool?>("reuse_views_when_allowed") ?? true;

            if (!Lookup.TrySheet(doc, sourceId, sourceNumber, out var source, out var failure)) return failure;
            if (SheetsHelpers.SheetNumberInUse(doc, newNumber, out var existing))
                return CommandResult.Fail("SHEET_NUMBER_IN_USE: sheet number '" + existing.SheetNumber + "' already exists.",
                    new { existingSheetId = RevitCompat.GetId(existing.Id) });

            var warnings = new List<string>();
            var viewports = new List<object>();
            var schedules = new List<object>();
            var revisionIds = new List<long>();
            var sourceTitleBlocks = SheetsHelpers.TitleBlocksOn(doc, source);
            var sourceTitleBlock = sourceTitleBlocks.FirstOrDefault();
            if (sourceTitleBlocks.Count > 1)
                warnings.Add("The source sheet has " + sourceTitleBlocks.Count + " title blocks; only the first was copied.");

            ViewSheet sheet;
            using (var tx = new Transaction(doc, "Plinth: Duplicate Sheet"))
            {
                tx.Start();
                try
                {
                    sheet = ViewSheet.Create(doc, sourceTitleBlock?.GetTypeId() ?? ElementId.InvalidElementId);
                    sheet.SheetNumber = newNumber;
                    sheet.Name = string.IsNullOrWhiteSpace(newName) ? source.Name : newName;

                    if (sourceTitleBlock != null)
                        CopyTitleBlock(doc, sourceTitleBlock, SheetsHelpers.TitleBlocksOn(doc, sheet).FirstOrDefault(), warnings);

                    foreach (var vpId in source.GetAllViewports())
                    {
                        if (!(doc.GetElement(vpId) is Viewport vp) || !(doc.GetElement(vp.ViewId) is View sourceView)) continue;
                        var center = vp.GetBoxCenter();
                        var targetViewId = vp.ViewId;
                        var mode = "reused_view";
                        if (!(reuse && Viewport.CanAddViewToSheet(doc, sheet.Id, vp.ViewId)))
                        {
                            try { targetViewId = sourceView.Duplicate(dupOption); }
                            catch (Exception ex) { throw new InvalidOperationException("could not duplicate view '" + sourceView.Name + "' (" + option + "): " + ex.Message, ex); }
                            mode = "duplicated_view";
                        }
                        var newVp = Viewport.Create(doc, sheet.Id, targetViewId, center);
                        if (newVp.GetTypeId() != vp.GetTypeId()) newVp.ChangeTypeId(vp.GetTypeId());
                        if (vp.Rotation != ViewportRotation.None)
                        {
                            newVp.Rotation = vp.Rotation;
                            newVp.SetBoxCenter(center);
                        }
                        viewports.Add(new
                        {
                            source_viewport_id = RevitCompat.GetId(vp.Id),
                            source_view_id = RevitCompat.GetId(vp.ViewId),
                            new_view_id = RevitCompat.GetId(targetViewId),
                            new_viewport_id = RevitCompat.GetId(newVp.Id),
                            center = new { x_mm = Mm.FromFeet(center.X, 3), y_mm = Mm.FromFeet(center.Y, 3) },
                            mode
                        });
                    }

                    if (includeSchedules)
                    {
                        foreach (var instance in new FilteredElementCollector(doc, source.Id).OfClass(typeof(ScheduleSheetInstance))
                                     .Cast<ScheduleSheetInstance>().Where(s => !s.IsTitleblockRevisionSchedule))
                        {
                            try
                            {
                                var copy = ScheduleSheetInstance.Create(doc, sheet.Id, instance.ScheduleId, instance.Point);
                                schedules.Add(new
                                {
                                    source_schedule_instance_id = RevitCompat.GetId(instance.Id),
                                    schedule_id = RevitCompat.GetId(instance.ScheduleId),
                                    new_schedule_instance_id = RevitCompat.GetId(copy.Id),
                                    point = new { x_mm = Mm.FromFeet(instance.Point.X, 3), y_mm = Mm.FromFeet(instance.Point.Y, 3) }
                                });
                            }
                            catch (Exception ex)
                            {
                                warnings.Add("Could not place schedule '" + doc.GetElement(instance.ScheduleId)?.Name + "': " + ex.Message);
                            }
                        }
                    }

                    if (includeRevisions)
                    {
                        var additional = source.GetAdditionalRevisionIds();
                        if (additional != null && additional.Count > 0)
                        {
                            sheet.SetAdditionalRevisionIds(additional);
                            revisionIds.AddRange(additional.Select(RevitCompat.GetId));
                        }
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures. Nothing was created.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("DUPLICATE_FAILED: could not duplicate sheet " + source.SheetNumber + ": " + ex.Message + " Nothing was created.");
                }
            }

            return CommandResult.Ok(new
            {
                source_sheet_id = RevitCompat.GetId(source.Id),
                new_sheet_id = RevitCompat.GetId(sheet.Id),
                new_sheet_number = sheet.SheetNumber,
                new_sheet_name = sheet.Name,
                title_block_type_id = sourceTitleBlock == null ? (long?)null : RevitCompat.GetId(sourceTitleBlock.GetTypeId()),
                viewports,
                schedules,
                revision_ids = revisionIds,
                warnings
            });
        }

        /// <summary>Moves the new title block onto the source's position and copies its writable instance parameters.</summary>
        private static void CopyTitleBlock(Document doc, FamilyInstance from, FamilyInstance to, List<string> warnings)
        {
            if (to == null)
            {
                warnings.Add("Revit placed no title block on the new sheet.");
                return;
            }
            if (from.Location is LocationPoint a && to.Location is LocationPoint b && !a.Point.IsAlmostEqualTo(b.Point))
                ElementTransformUtils.MoveElement(doc, to.Id, a.Point - b.Point);

            foreach (Parameter param in from.Parameters)
            {
                if (param.IsReadOnly || !param.HasValue || param.Definition == null) continue;
                // Sheet number and name on the title block mirror the sheet itself, already set; copying the
                // source values would collide at commit ("Sheet Number is already in use").
                var builtIn = (param.Definition as InternalDefinition)?.BuiltInParameter;
                if (builtIn == BuiltInParameter.SHEET_NUMBER || builtIn == BuiltInParameter.SHEET_NAME) continue;
                var target = to.get_Parameter(param.Definition);
                if (target == null || target.IsReadOnly) continue;
                try
                {
                    switch (param.StorageType)
                    {
                        case StorageType.String: target.Set(param.AsString() ?? ""); break;
                        case StorageType.Integer: target.Set(param.AsInteger()); break;
                        case StorageType.Double: target.Set(param.AsDouble()); break;
                        case StorageType.ElementId: target.Set(param.AsElementId()); break;
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add("Could not copy title block parameter '" + param.Definition.Name + "': " + ex.Message);
                }
            }
        }
    }
}
