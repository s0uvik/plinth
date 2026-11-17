// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Workflows/WorkflowNamingNormalizationHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: ids are applied before the limit (rvt-mcp cut the collection at limit first, so ids past it
// were silently missed) and ids not in the target scope are an error listing them (rvt-mcp warned); the unused
// profile field is dropped (rvt-mcp only echoed it); {ElevationMm} goes through Mm; the proposal lists are capped
// by limit (at most 1000); the rename transaction is "Plinth: ..." and any Revit warning rolls it back (FailureScope).

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Workflows
{
    using View = Autodesk.Revit.DB.View;

    public sealed class WorkflowNamingNormalizationHandler : IRevitCommand
    {
        public string Name => "workflow_naming_normalization";
        public string Description =>
            "Propose and optionally apply renames for views, sheets, levels or grids. Without pattern, names are tidied (trimmed, double spaces "
            + "collapsed, characters Revit forbids replaced by '-'). pattern tokens: {Name} {Index} {Id} {Category} {Type} {SheetNumber} {ElevationMm}. "
            + "ids limits the targets; dry_run (default true) only proposes; conflicts block the apply.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""target"":{""type"":""string"",""enum"":[""views"",""sheets"",""levels"",""grids"",""all""]},
            ""pattern"":{""type"":""string""},
            ""ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""dry_run"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}},
            ""required"":[""target""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var target = p.Value<string>("target");
            var pattern = p.Value<string>("pattern");
            var dryRun = p.Value<bool?>("dry_run") ?? true;
            var limit = p.Value<int?>("limit") ?? 200;
            var ids = p["ids"]?.ToObject<long[]>();
            if (limit < 1 || limit > 1000) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to 1000.");

            var warnings = new List<string>();
            var idFilter = ids == null || ids.Length == 0 ? null : new HashSet<long>(ids);
            var elements = new List<Element>();
            var truncated = false;
            var inScope = new HashSet<long>();
            foreach (var element in Targets(doc, target))
            {
                var rawId = RevitCompat.GetId(element.Id);
                if (idFilter != null && !idFilter.Contains(rawId)) continue;
                inScope.Add(rawId);
                if (IsProtected(element, out var reason))
                {
                    warnings.Add("Skipped " + rawId + ": " + reason);
                    continue;
                }
                if (elements.Count >= limit)
                {
                    truncated = true;
                    continue;
                }
                elements.Add(element);
            }
            if (idFilter != null)
            {
                var missing = idFilter.Where(id => !inScope.Contains(id)).ToList();
                if (missing.Count > 0)
                    return CommandResult.Fail("ELEMENT_NOT_FOUND: these ids are not " + target + " in this document: " + string.Join(", ", missing.Take(100)) + ".");
            }
            if (truncated) warnings.Add("Targets were cut at limit " + limit + "; run again with ids or a higher limit for the rest.");

            var proposed = Plan(doc, elements, pattern, out var conflicts, out var skipped);
            var steps = new JArray
            {
                WorkflowsHelpers.Step("Analyze Names", "name checks", "succeeded", "Collect targets and build rename proposals.",
                    new { target, collected = elements.Count, proposed = proposed.Count, conflicts = conflicts.Count, skipped = skipped.Count, truncated })
            };

            if (dryRun)
                return CommandResult.Ok(Result(true, conflicts.Count > 0 ? "partial" : "succeeded", steps, warnings, proposed, new JArray(), conflicts, skipped,
                    new long[0], WorkflowsHelpers.Rollback("Transaction", false, "Dry run; no transaction opened.")));

            if (conflicts.Count > 0)
            {
                steps.Add(WorkflowsHelpers.Step("Apply Renames", "Element.Name", "failed", "Renames are blocked by name conflicts.", null,
                    "Resolve the conflicts (or narrow ids) before dry_run false."));
                return CommandResult.Ok(Result(false, "failed", steps, warnings, proposed, new JArray(), conflicts, skipped, new long[0],
                    WorkflowsHelpers.Rollback("Transaction", false, "Validation failed; nothing was renamed.")));
            }
            if (proposed.Count == 0)
                return CommandResult.Ok(Result(false, "succeeded", steps, warnings, proposed, new JArray(), conflicts, skipped, new long[0],
                    WorkflowsHelpers.Rollback("None", false, "Nothing to rename.")));

            var applied = new JArray();
            var modified = new List<long>();
            using (var tx = new Transaction(doc, "Plinth: Workflow naming normalization"))
            {
                tx.Start();
                try
                {
                    foreach (var proposal in proposed.OfType<JObject>())
                    {
                        var rawId = proposal.Value<long>("element_id");
                        doc.GetElement(RevitCompat.ToElementId(rawId)).Name = proposal.Value<string>("proposed_name");
                        modified.Add(rawId);
                        applied.Add(proposal.DeepClone());
                    }
                    if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit rolled the renames back; see failures.");
                    steps.Add(WorkflowsHelpers.Step("Apply Renames", "Element.Name", "succeeded", "Applied every proposed rename.", new { renamed = applied.Count }));
                    return CommandResult.Ok(Result(false, "succeeded", steps, warnings, proposed, applied, conflicts, skipped, modified,
                        WorkflowsHelpers.Rollback("Transaction", false, "Renames committed.")));
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    steps.Add(WorkflowsHelpers.Step("Apply Renames", "Element.Name", "failed", "Apply the proposed renames.", null, ex.Message));
                    return CommandResult.Ok(Result(false, "failed", steps, warnings, proposed, new JArray(), conflicts, skipped, new long[0],
                        WorkflowsHelpers.Rollback("Transaction", true, "A rename failed; all renames were rolled back.")));
                }
            }
        }

        private JObject Result(bool dryRun, string status, JArray steps, IEnumerable<string> warnings, JArray proposed, JArray applied,
            JArray conflicts, JArray skipped, IEnumerable<long> modified, JObject rollback)
        {
            var result = WorkflowsHelpers.Envelope(Name, dryRun, status, steps, new long[0], modified, warnings.Take(200), rollback);
            result["proposed_renames"] = proposed;
            result["applied_renames"] = applied;
            result["conflicts"] = conflicts;
            result["skipped_items"] = skipped;
            return result;
        }

        private static IEnumerable<Element> Targets(Document doc, string target)
        {
            if (target == "views" || target == "all")
                foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                    if (!(view is ViewSheet)) yield return view;
            if (target == "sheets" || target == "all")
                foreach (var sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)))
                    yield return sheet;
            if (target == "levels" || target == "all")
                foreach (var level in new FilteredElementCollector(doc).OfClass(typeof(Level)))
                    yield return level;
            if (target == "grids" || target == "all")
                foreach (var grid in new FilteredElementCollector(doc).OfClass(typeof(Grid)))
                    yield return grid;
        }

        private static bool IsProtected(Element element, out string reason)
        {
            reason = null;
            if (element is View view)
            {
                if (view.IsTemplate) reason = "view templates are not renamed here.";
                else if (view.ViewType == ViewType.ProjectBrowser || view.ViewType == ViewType.SystemBrowser || view.ViewType == ViewType.Internal)
                    reason = "system views cannot be renamed.";
            }
            return reason != null;
        }

        private static string KindOf(Element element) =>
            element is ViewSheet ? "sheets" : element is View ? "views" : element is Level ? "levels" : element is Grid ? "grids" : "elements";

        private static JArray Plan(Document doc, List<Element> elements, string pattern, out JArray conflicts, out JArray skipped)
        {
            conflicts = new JArray();
            skipped = new JArray();
            var proposed = new JArray();
            var existing = new Dictionary<string, Dictionary<string, long>>();
            foreach (var element in Targets(doc, "all"))
            {
                var name = WorkflowsHelpers.SafeName(element);
                if (string.IsNullOrWhiteSpace(name)) continue;
                var kind = KindOf(element);
                if (!existing.TryGetValue(kind, out var names)) existing[kind] = names = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                if (!names.ContainsKey(name)) names[name] = RevitCompat.GetId(element.Id);
            }
            var planned = new Dictionary<string, HashSet<string>>();

            for (var i = 0; i < elements.Count; i++)
            {
                var element = elements[i];
                var kind = KindOf(element);
                var id = RevitCompat.GetId(element.Id);
                var current = WorkflowsHelpers.SafeName(element) ?? "";
                var next = WorkflowsHelpers.ApplyPattern(pattern, element, i + 1);
                var row = new JObject { ["element_id"] = id, ["kind"] = kind, ["current_name"] = current };
                if (string.IsNullOrWhiteSpace(next))
                {
                    row["reason"] = "the proposed name is blank.";
                    skipped.Add(row);
                    continue;
                }
                if (string.Equals(current, next, StringComparison.Ordinal))
                {
                    row["reason"] = "already normalized.";
                    skipped.Add(row);
                    continue;
                }
                row["proposed_name"] = next;
                if (!planned.TryGetValue(kind, out var set)) planned[kind] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!set.Add(next))
                {
                    row["reason"] = "another target gets the same proposed name.";
                    conflicts.Add(row);
                    continue;
                }
                if (existing.TryGetValue(kind, out var names) && names.TryGetValue(next, out var holder) && holder != id)
                {
                    row["reason"] = "the name is already used by element " + holder + ".";
                    conflicts.Add(row);
                    continue;
                }
                proposed.Add(row);
            }
            return proposed;
        }
    }
}
