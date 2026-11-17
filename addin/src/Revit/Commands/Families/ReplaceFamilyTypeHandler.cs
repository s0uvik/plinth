// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Families/ReplaceFamilyTypeHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: ids are integers; replacing a type with itself is refused; view_id must name a view (no silent
// active-view fallback when it is wrong); instances that refuse the type are reported as failedCount so batch_execute
// rolls a partial replacement back (rvt-mcp listed them under "errors"); categories are compared by id.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Families
{
    public sealed class ReplaceFamilyTypeHandler : IRevitCommand
    {
        public string Name => "replace_family_type";
        public string Description =>
            "Change every instance of type from_type_id to to_type_id (same category) in scope all, active_view (or view_id) or the "
            + "current selection. dry_run counts only. Instances that cannot take the new type are listed and counted in failedCount.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""from_type_id"":{""type"":""integer""},
            ""to_type_id"":{""type"":""integer""},
            ""scope"":{""type"":""string"",""enum"":[""all"",""active_view"",""selection""]},
            ""view_id"":{""type"":""integer""},
            ""dry_run"":{""type"":""boolean""}},""required"":[""from_type_id"",""to_type_id""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!FamiliesHelpers.TryType(doc, p.Value<long>("from_type_id"), "from_type_id", out var from, out var failure)) return failure;
            if (!FamiliesHelpers.TryType(doc, p.Value<long>("to_type_id"), "to_type_id", out var to, out failure)) return failure;
            if (from.Id == to.Id) return CommandResult.Fail("INVALID_PARAMS: from_type_id and to_type_id are the same type.");
            if (from.Category == null || to.Category == null || from.Category.Id != to.Category.Id)
                return CommandResult.Fail("CATEGORY_MISMATCH: '" + from.Name + "' is " + (from.Category?.Name ?? "uncategorised")
                    + " but '" + to.Name + "' is " + (to.Category?.Name ?? "uncategorised") + "; both types must be in the same category.");

            var scope = p.Value<string>("scope") ?? "all";
            var dryRun = p.Value<bool?>("dry_run") ?? false;
            var viewId = p.Value<long?>("view_id");
            if (viewId.HasValue && scope != "active_view")
                return CommandResult.Fail("INVALID_PARAMS: view_id goes with scope \"active_view\".");

            IEnumerable<Element> candidates;
            if (scope == "selection")
            {
                candidates = uidoc.Selection.GetElementIds().Select(doc.GetElement).Where(e => e != null);
            }
            else if (scope == "active_view")
            {
                Autodesk.Revit.DB.View view;
                if (viewId.HasValue)
                {
                    if (!Lookup.TryView(doc, viewId.Value, out view, out failure)) return failure;
                }
                else
                {
                    view = doc.ActiveView;
                    if (view == null) return CommandResult.Fail("NO_ACTIVE_VIEW: there is no active view; pass view_id.");
                }
                candidates = new FilteredElementCollector(doc, view.Id).OfCategoryId(from.Category.Id).WhereElementIsNotElementType();
            }
            else
            {
                candidates = new FilteredElementCollector(doc).OfCategoryId(from.Category.Id).WhereElementIsNotElementType();
            }
            var instances = candidates.Where(e => e.GetTypeId() == from.Id).ToList();

            var result = new JObject
            {
                ["from_type_id"] = RevitCompat.GetId(from.Id),
                ["from_type_name"] = from.Name,
                ["to_type_id"] = RevitCompat.GetId(to.Id),
                ["to_type_name"] = to.Name,
                ["category"] = from.Category.Name,
                ["scope"] = scope,
                ["instance_count"] = instances.Count,
                ["dry_run"] = dryRun
            };
            if (dryRun || instances.Count == 0)
            {
                result["changed"] = 0;
                result["failedCount"] = 0;
                return CommandResult.Ok(result);
            }

            var changed = new List<long>();
            var failed = new JArray();
            using (var tx = new Transaction(doc, "Plinth: Replace type"))
            {
                tx.Start();
                try
                {
                    if (to is FamilySymbol symbol && !symbol.IsActive)
                    {
                        symbol.Activate();
                        doc.Regenerate();
                    }
                    foreach (var e in instances)
                    {
                        var id = RevitCompat.GetId(e.Id);
                        try
                        {
                            if (!e.IsValidType(to.Id))
                            {
                                failed.Add(new JObject { ["instance_id"] = id, ["error"] = "the new type is not valid for this instance (host or family mismatch)" });
                                continue;
                            }
                            e.ChangeTypeId(to.Id);
                            changed.Add(id);
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new JObject { ["instance_id"] = id, ["error"] = ex.Message });
                        }
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the replacement back; see failures. Nothing changed.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("REPLACE_FAILED: " + ex.Message);
                }
            }
            result["changed"] = changed.Count;
            result["changed_ids"] = new JArray(changed.Take(1000));
            result["failedCount"] = failed.Count;
            result["failed"] = failed;
            return CommandResult.Ok(result);
        }
    }
}
