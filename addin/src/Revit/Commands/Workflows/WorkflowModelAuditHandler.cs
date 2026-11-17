// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Workflows/WorkflowModelAuditHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: statistics come from analyze_model_statistics and warnings from get_model_warnings_summary
// (both called in-process; rvt-mcp counted only limit_per_section elements for the statistics); the
// warning risk class is still derived from the description text and Revit's own severity is reported next to
// it; family instances are counted from a FamilyInstance collector instead of every element; categories carry
// builtInCategory; limit_per_section is at most 500 (rvt-mcp 1000) so the response stays well under 1 MiB.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Plinth.Revit.Commands.Lint;
using Plinth.Revit.Commands.Query;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Workflows
{
    using View = Autodesk.Revit.DB.View;

    public sealed class WorkflowModelAuditHandler : IRevitCommand
    {
        public string Name => "workflow_model_audit";
        public string Description =>
            "Read-only model audit: element statistics, warnings (risk-classified), families (unused, in-place, many types, duplicate names), "
            + "views (unplaced printable, duplicate names), empty schedules and MEP connectors left open. limit_per_section 1 to 500 (default 100).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""include_warnings"":{""type"":""boolean""},
            ""include_families"":{""type"":""boolean""},
            ""include_views"":{""type"":""boolean""},
            ""include_schedules"":{""type"":""boolean""},
            ""include_mep"":{""type"":""boolean""},
            ""limit_per_section"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var includeWarnings = p.Value<bool?>("include_warnings") ?? true;
            var includeFamilies = p.Value<bool?>("include_families") ?? true;
            var includeViews = p.Value<bool?>("include_views") ?? true;
            var includeSchedules = p.Value<bool?>("include_schedules") ?? true;
            var includeMep = p.Value<bool?>("include_mep") ?? true;
            var limit = p.Value<int?>("limit_per_section") ?? 100;
            if (!includeWarnings && !includeFamilies && !includeViews && !includeSchedules && !includeMep)
                return CommandResult.Fail("INVALID_PARAMS: at least one include_* flag must be true.");
            if (limit < 1 || limit > 500) return CommandResult.Fail("INVALID_PARAMS: limit_per_section must be 1 to 500.");

            var steps = new JArray();
            var sections = new JObject();
            var warnings = new List<string>();
            int high = 0, medium = 0, low = 0;
            var truncated = false;

            var stats = WorkflowsHelpers.Invoke(new AnalyzeModelStatisticsHandler(), app, new JObject());
            if (stats.Success)
            {
                var data = (JObject)stats.Data;
                truncated |= data.Value<bool>("truncated");
                sections["statistics"] = data;
                steps.Add(WorkflowsHelpers.Step("Model Statistics", "analyze_model_statistics", "succeeded", "Count model elements by category.",
                    new JObject { ["elements_counted"] = data["elementsCounted"], ["truncated"] = data["truncated"] }));
            }
            else
            {
                warnings.Add(stats.Error);
                steps.Add(WorkflowsHelpers.Step("Model Statistics", "analyze_model_statistics", "failed", "Count model elements by category.", null, stats.Error));
            }

            if (includeWarnings)
            {
                var result = WorkflowsHelpers.Invoke(new GetModelWarningsSummaryHandler(), app,
                    new JObject { ["include_examples"] = true, ["max_examples_per_type"] = 3, ["max_warning_types"] = limit });
                if (result.Success)
                {
                    var data = (JObject)result.Data;
                    var items = new JArray();
                    int h = 0, m = 0, l = 0;
                    foreach (var group in ((JArray)data["warnings"]).OfType<JObject>())
                    {
                        var groupRisk = Classify(group.Value<string>("description"));
                        var count = group.Value<int>("count");
                        if (groupRisk == "high") h += count; else if (groupRisk == "medium") m += count; else l += count;
                        group["risk"] = groupRisk;
                        items.Add(group);
                    }
                    high += h; medium += m; low += l;
                    truncated |= data.Value<bool>("truncated");
                    sections["warnings"] = new JObject
                    {
                        ["total"] = data["total_warnings"],
                        ["returned_types"] = data["returned_warning_types"],
                        ["truncated"] = data["truncated"],
                        ["high"] = h,
                        ["medium"] = m,
                        ["low"] = l,
                        ["items"] = items
                    };
                    steps.Add(WorkflowsHelpers.Step("Warnings", "get_model_warnings_summary", "succeeded", "Group Revit warnings by description and classify risk.",
                        new JObject { ["count"] = data["total_warnings"] }));
                }
                else
                {
                    warnings.Add(result.Error);
                    steps.Add(WorkflowsHelpers.Step("Warnings", "get_model_warnings_summary", "failed", "Group Revit warnings.", null, result.Error));
                }
            }

            if (includeFamilies)
            {
                var section = AuditFamilies(doc, limit, ref truncated);
                medium += section.Value<int>("unused_count");
                low += section.Value<int>("high_type_count");
                sections["families"] = section;
                steps.Add(WorkflowsHelpers.Step("Families", "family collectors", "succeeded",
                    "Find in-place, unused, duplicate-name and high-type-count families.", new { count = section.Value<int>("total_families") }));
            }

            if (includeViews)
            {
                var section = AuditViews(doc, limit, ref truncated);
                low += section.Value<int>("unplaced_printable_count") + section.Value<int>("duplicate_name_groups_count");
                sections["views"] = section;
                steps.Add(WorkflowsHelpers.Step("Views", "view collectors", "succeeded",
                    "Check templates, printable views, sheet placement and duplicate names.", new { count = section.Value<int>("total_views") }));
            }

            if (includeSchedules)
            {
                var section = AuditSchedules(doc, limit, ref truncated);
                low += section.Value<int>("empty_count");
                sections["schedules"] = section;
                steps.Add(WorkflowsHelpers.Step("Schedules", "schedule collectors", "succeeded",
                    "Check schedules, placement and empty bodies.", new { count = section.Value<int>("total_schedules") }));
            }

            if (includeMep)
            {
                var section = AuditMep(doc, limit, ref truncated);
                medium += section.Value<int>("disconnected_count");
                sections["mep"] = section;
                steps.Add(WorkflowsHelpers.Step("MEP", "connector checks", "succeeded",
                    "Sample MEP curves and equipment for open connectors.", new { count = section.Value<int>("checked") }));
            }

            var recommendations = new JArray();
            if (high > 0) recommendations.Add("Resolve high-risk warnings before issuing model deliverables.");
            if (medium > 0) recommendations.Add("Review medium-risk warnings, unused families and open MEP connectors before coordination.");
            if (low > 0) recommendations.Add("Use workflow_view_cleanup and workflow_naming_normalization to reduce low-risk hygiene findings.");
            if (recommendations.Count == 0) recommendations.Add("No audit findings in the selected sections.");
            if (truncated) warnings.Add("At least one section was truncated; raise limit_per_section or audit one section at a time.");

            var envelope = WorkflowsHelpers.Envelope(Name, false, "succeeded", steps, new long[0], new long[0], warnings,
                WorkflowsHelpers.Rollback("None", false, "Read-only audit; no transaction opened."));
            var risk = new JObject { ["high"] = high, ["medium"] = medium, ["low"] = low };
            envelope["summary"] = new JObject
            {
                ["document_title"] = doc.Title ?? "",
                ["status"] = high > 0 ? "high_risk" : medium > 0 ? "review_needed" : low > 0 ? "minor_findings" : "clean",
                ["finding_count"] = high + medium + low,
                ["risk_counts"] = risk
            };
            envelope["sections"] = sections;
            envelope["risk_counts"] = risk.DeepClone();
            envelope["recommendations"] = recommendations;
            envelope["truncated"] = truncated;
            return CommandResult.Ok(envelope);
        }

        private static string Classify(string description)
        {
            var text = (description ?? "").ToLowerInvariant();
            if (text.Contains("overlap") || text.Contains("duplicate") || text.Contains("identical") || (text.Contains("room") && text.Contains("not enclosed")))
                return "medium";
            if (text.Contains("cannot") || text.Contains("failed") || text.Contains("lost") || text.Contains("inconsistent"))
                return "high";
            return "low";
        }

        private sealed class FamilyInfo
        {
            public long Id;
            public string Name;
            public string Category;
            public string BuiltInCategory;
            public bool IsInPlace;
            public int TypeCount;
            public int InstanceCount;

            public JObject ToJson() => new JObject
            {
                ["family_id"] = Id,
                ["name"] = Name,
                ["category"] = Category,
                ["builtInCategory"] = BuiltInCategory,
                ["is_in_place"] = IsInPlace,
                ["type_count"] = TypeCount,
                ["instance_count"] = InstanceCount
            };
        }

        private static JObject AuditFamilies(Document doc, int limit, ref bool truncated)
        {
            var infos = new Dictionary<long, FamilyInfo>();
            var symbolToFamily = new Dictionary<long, long>();
            foreach (var family in new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>())
            {
                var id = RevitCompat.GetId(family.Id);
                var info = new FamilyInfo { Id = id, Name = family.Name ?? "" };
                try
                {
                    info.Category = family.FamilyCategory?.Name ?? "Uncategorized";
                    info.BuiltInCategory = Lookup.BuiltInName(family.FamilyCategory);
                    info.IsInPlace = family.IsInPlace;
                    var symbols = family.GetFamilySymbolIds();
                    info.TypeCount = symbols.Count;
                    foreach (var sid in symbols) symbolToFamily[RevitCompat.GetId(sid)] = id;
                }
                catch
                {
                    // Families that cannot be read are listed with what was read.
                }
                infos[id] = info;
            }
            foreach (var instance in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
            {
                var typeId = RevitCompat.GetId(instance.GetTypeId());
                if (symbolToFamily.TryGetValue(typeId, out var familyId)) infos[familyId].InstanceCount++;
            }

            var all = infos.Values.ToList();
            var unused = all.Where(f => f.InstanceCount == 0).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var inPlace = all.Where(f => f.IsInPlace).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var highType = all.Where(f => f.TypeCount >= 20).OrderByDescending(f => f.TypeCount).ToList();
            var duplicates = all.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrEmpty(g.Key) && g.Count() > 1).ToList();
            if (unused.Count > limit || inPlace.Count > limit || highType.Count > limit || duplicates.Count > limit) truncated = true;

            return new JObject
            {
                ["total_families"] = all.Count,
                ["unused_count"] = unused.Count,
                ["inplace_count"] = inPlace.Count,
                ["high_type_count"] = highType.Count,
                ["duplicate_name_groups_count"] = duplicates.Count,
                ["unused"] = new JArray(unused.Take(limit).Select(f => f.ToJson())),
                ["inplace"] = new JArray(inPlace.Take(limit).Select(f => f.ToJson())),
                ["high_type_count_families"] = new JArray(highType.Take(limit).Select(f => f.ToJson())),
                ["duplicate_name_groups"] = new JArray(duplicates.Take(limit).Select(g => new JObject
                {
                    ["name"] = g.Key,
                    ["count"] = g.Count(),
                    ["ids"] = new JArray(g.Select(f => f.Id))
                }))
            };
        }

        private static HashSet<long> PlacedViewIds(Document doc)
        {
            var ids = new HashSet<long>();
            foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()) ids.Add(RevitCompat.GetId(vp.ViewId));
            foreach (var ssi in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
                ids.Add(RevitCompat.GetId(ssi.ScheduleId));
            return ids;
        }

        private static bool CanBePrinted(View view)
        {
            try { return view.CanBePrinted; }
            catch { return false; }
        }

        private static JObject AuditViews(Document doc, int limit, ref bool truncated)
        {
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
            var placed = PlacedViewIds(doc);
            var printable = views.Where(v => !v.IsTemplate && CanBePrinted(v)).ToList();
            var unplaced = printable.Where(v => v.ViewType != ViewType.DrawingSheet && !placed.Contains(RevitCompat.GetId(v.Id)))
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var duplicates = printable.GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrEmpty(g.Key) && g.Count() > 1).ToList();
            if (unplaced.Count > limit || duplicates.Count > limit) truncated = true;
            return new JObject
            {
                ["total_views"] = views.Count,
                ["templates"] = views.Count(v => v.IsTemplate),
                ["printable"] = printable.Count,
                ["unplaced_printable_count"] = unplaced.Count,
                ["duplicate_name_groups_count"] = duplicates.Count,
                ["unplaced_printable"] = new JArray(unplaced.Take(limit).Select(v => new JObject
                {
                    ["view_id"] = RevitCompat.GetId(v.Id),
                    ["name"] = v.Name,
                    ["view_type"] = v.ViewType.ToString()
                })),
                ["duplicate_name_groups"] = new JArray(duplicates.Take(limit).Select(g => new JObject
                {
                    ["name"] = g.Key,
                    ["count"] = g.Count(),
                    ["ids"] = new JArray(g.Select(v => RevitCompat.GetId(v.Id)))
                }))
            };
        }

        internal static bool IsScheduleEmpty(ViewSchedule schedule)
        {
            try
            {
                var body = schedule.GetTableData().GetSectionData(SectionType.Body);
                return body == null || body.NumberOfRows <= 1;
            }
            catch
            {
                return false;
            }
        }

        private static JObject AuditSchedules(Document doc, int limit, ref bool truncated)
        {
            var schedules = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule).ToList();
            var placed = PlacedViewIds(doc);
            var empty = schedules.Where(IsScheduleEmpty).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (empty.Count > limit) truncated = true;
            return new JObject
            {
                ["total_schedules"] = schedules.Count,
                ["placed_count"] = schedules.Count(s => placed.Contains(RevitCompat.GetId(s.Id))),
                ["empty_count"] = empty.Count,
                ["empty"] = new JArray(empty.Take(limit).Select(s => new JObject
                {
                    ["schedule_id"] = RevitCompat.GetId(s.Id),
                    ["name"] = s.Name,
                    ["is_placed"] = placed.Contains(RevitCompat.GetId(s.Id))
                }))
            };
        }

        private static JObject AuditMep(Document doc, int limit, ref bool truncated)
        {
            var candidates = new List<Element>();
            var sampled = false;
            foreach (var bic in new[]
            {
                BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_CableTray, BuiltInCategory.OST_Conduit,
                BuiltInCategory.OST_DuctTerminal, BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_ElectricalEquipment,
                BuiltInCategory.OST_PlumbingFixtures
            })
            {
                foreach (var element in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
                {
                    if (candidates.Count >= limit)
                    {
                        sampled = true;
                        break;
                    }
                    candidates.Add(element);
                }
                if (sampled) break;
            }
            if (sampled) truncated = true;

            var disconnected = new JArray();
            foreach (var element in candidates)
            {
                var open = OpenConnectors(element);
                if (open > 0)
                    disconnected.Add(new JObject
                    {
                        ["element_id"] = RevitCompat.GetId(element.Id),
                        ["name"] = WorkflowsHelpers.SafeName(element),
                        ["category"] = WorkflowsHelpers.SafeCategoryName(element),
                        ["disconnected_connectors"] = open
                    });
            }
            return new JObject
            {
                ["checked"] = candidates.Count,
                ["sampled"] = sampled,
                ["disconnected_count"] = disconnected.Count,
                ["disconnected"] = disconnected
            };
        }

        private static int OpenConnectors(Element element)
        {
            try
            {
                ConnectorSet connectors = null;
                if (element is FamilyInstance fi) connectors = fi.MEPModel?.ConnectorManager?.Connectors;
                if (element is MEPCurve curve) connectors = curve.ConnectorManager?.Connectors;
                if (connectors == null) return 0;
                var open = 0;
                foreach (Connector c in connectors)
                {
                    try
                    {
                        if (!c.IsConnected) open++;
                    }
                    catch
                    {
                        // Logical connectors without a connection state are skipped.
                    }
                }
                return open;
            }
            catch
            {
                return 0;
            }
        }
    }
}
