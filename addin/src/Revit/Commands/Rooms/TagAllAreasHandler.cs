// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Rooms/TagAllAreasHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: follows tag_all_rooms: area plan misses list the valid area plans; tag_type_id
// must be an area tag type; a project without an area tag family fails up front; when no tag could be
// placed, nothing is kept and the call fails; already tagged areas are counted (rvt-mcp's
// skipped_existing_count stayed 0 because they were filtered out first); placed areas only.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Rooms
{
    public sealed class TagAllAreasHandler : IRevitCommand
    {
        private const int PreviewCount = 20;

        public string Name => "tag_all_areas";
        public string Description =>
            "Tag the untagged placed areas of an area plan (area_plan_view_id or area_plan_view_name, default the active view) "
            + "at each area's location point. Optional tag_type_id; skip_existing (default true); limit (default 500, max 5000).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""area_plan_view_id"":{""type"":""integer""},
            ""area_plan_view_name"":{""type"":""string""},
            ""skip_existing"":{""type"":""boolean""},
            ""tag_type_id"":{""type"":""integer""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var planId = p.Value<long?>("area_plan_view_id");
            var planName = p.Value<string>("area_plan_view_name");
            var skipExisting = p.Value<bool?>("skip_existing") ?? true;
            var tagTypeId = p.Value<long?>("tag_type_id");
            var limit = p.Value<int?>("limit") ?? 500;
            if (limit < 1 || limit > 5000) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to 5000.");
            if (planId.HasValue && !string.IsNullOrEmpty(planName))
                return CommandResult.Fail("INVALID_PARAMS: pass area_plan_view_id or area_plan_view_name, not both.");

            ViewPlan plan;
            CommandResult failure;
            if (planId.HasValue || !string.IsNullOrEmpty(planName))
            {
                if (!RoomsHelpers.TryAreaPlan(doc, planId, planName, out plan, out failure)) return failure;
            }
            else
            {
                plan = app.ActiveUIDocument?.ActiveView as ViewPlan;
                if (plan == null || plan.ViewType != ViewType.AreaPlan)
                    return CommandResult.Fail("NOT_AN_AREA_PLAN: the active view is not an area plan; pass area_plan_view_id or area_plan_view_name.",
                        new JObject { ["valid_area_plans"] = RoomsHelpers.DescribePlans(RoomsHelpers.AreaPlans(doc)) });
            }

            FamilySymbol tagType = null;
            if (tagTypeId.HasValue)
            {
                tagType = doc.GetElement(RevitCompat.ToElementId(tagTypeId.Value)) as FamilySymbol;
                if (tagType == null || tagType.Category?.BuiltInCategory != BuiltInCategory.OST_AreaTags)
                    return CommandResult.Fail("TYPE_NOT_FOUND: " + tagTypeId.Value + " is not an area tag type.", new JObject
                    {
                        ["valid_area_tag_types"] = new JArray(new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                            .OfCategory(BuiltInCategory.OST_AreaTags).Cast<FamilySymbol>().Take(100)
                            .Select(s => new JObject { ["id"] = RevitCompat.GetId(s.Id), ["name"] = s.FamilyName + ": " + s.Name }))
                    });
            }
            else if (new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_AreaTags).FirstElement() == null)
            {
                return CommandResult.Fail("NO_AREA_TAG_TYPE: no area tag family is loaded in the project.");
            }

            var areas = new FilteredElementCollector(doc, plan.Id).OfCategory(BuiltInCategory.OST_Areas).WhereElementIsNotElementType()
                .OfType<Area>().Where(a => a.Area > 0.0001 && a.Location is LocationPoint).ToList();
            var tagged = new HashSet<long>();
            foreach (var tag in new FilteredElementCollector(doc, plan.Id).OfCategory(BuiltInCategory.OST_AreaTags)
                         .WhereElementIsNotElementType().OfType<AreaTag>())
            {
                var area = tag.Area;
                if (area != null) tagged.Add(RevitCompat.GetId(area.Id));
            }
            var candidates = areas.Where(a => !skipExisting || !tagged.Contains(RevitCompat.GetId(a.Id)))
                .OrderBy(a => RevitCompat.GetId(a.Id)).ToList();
            var batch = candidates.Take(limit).ToList();
            var planInfo = RoomsHelpers.DescribePlan(plan);
            if (batch.Count == 0)
                return CommandResult.Ok(new JObject
                {
                    ["area_plan"] = planInfo,
                    ["placed_areas"] = areas.Count,
                    ["already_tagged"] = areas.Count(a => tagged.Contains(RevitCompat.GetId(a.Id))),
                    ["created_count"] = 0,
                    ["message"] = areas.Count == 0 ? "No placed areas in this area plan." : "Every placed area in this area plan is already tagged."
                });

            var created = new List<long>();
            var failed = new JArray();
            var failedCount = 0;
            using (var tx = new Transaction(doc, "B2M: Tag All Areas"))
            {
                tx.Start();
                try
                {
                    foreach (var area in batch)
                    {
                        try
                        {
                            var point = ((LocationPoint)area.Location).Point;
                            var tag = doc.Create.NewAreaTag(plan, area, new UV(point.X, point.Y));
                            if (tag == null) throw new InvalidOperationException("Revit returned no tag.");
                            if (tagType != null && tag.GetTypeId() != tagType.Id) tag.ChangeTypeId(tagType.Id);
                            created.Add(RevitCompat.GetId(tag.Id));
                        }
                        catch (Exception ex)
                        {
                            failedCount++;
                            if (failed.Count < PreviewCount)
                                failed.Add(new JObject { ["area_id"] = RevitCompat.GetId(area.Id), ["error"] = ex.Message });
                        }
                    }
                    if (created.Count == 0)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("TAG_FAILED: no area could be tagged; see failed. Nothing was created.",
                            new JObject { ["area_plan"] = planInfo, ["failed_count"] = failedCount, ["failed"] = failed });
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures. Nothing was created.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("TAG_FAILED: could not tag the areas: " + ex.Message + " Nothing was created.");
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["area_plan"] = planInfo,
                ["placed_areas"] = areas.Count,
                ["already_tagged"] = areas.Count(a => tagged.Contains(RevitCompat.GetId(a.Id))),
                ["candidate_count"] = candidates.Count,
                ["processed_count"] = batch.Count,
                ["created_count"] = created.Count,
                ["failed_count"] = failedCount,
                ["truncated"] = candidates.Count > batch.Count,
                ["remaining_candidate_count"] = candidates.Count - batch.Count,
                ["tag_type_id"] = tagType == null ? null : new JValue(RevitCompat.GetId(tagType.Id)),
                ["created_tag_ids"] = new JArray(created),
                ["failed"] = failed,
                ["failures_truncated"] = failedCount > failed.Count
            });
        }
    }
}
