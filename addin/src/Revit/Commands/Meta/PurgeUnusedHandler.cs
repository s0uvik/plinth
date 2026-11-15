// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Documents/PurgeUnusedHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: candidates come from Revit's own purge test (Document.GetUnusedElements, Revit 2024+) filtered to
// loadable family types, instead of rvt-mcp's "no placed instance" heuristic, so types used by tags, schedules or
// other families are kept; in-place family types are still skipped. The unused "targets" option is dropped (family
// types are the only target); a delete that Revit refuses is listed per type; a partial delete is reported as
// failedCount so batch_execute rolls it back. dry_run still defaults to true.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Meta
{
    public sealed class PurgeUnusedHandler : IRevitCommand
    {
        private const int MaxLimit = 500;

        public string Name => "purge_unused";
        public string Description => "Purge unused loadable family types (only those Revit itself reports as purgeable). "
            + "dry_run defaults to true and only lists what would be deleted; pass dry_run=false to delete.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""dry_run"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject parameters)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            var dryRun = parameters.Value<bool?>("dry_run") ?? true;
            var limit = parameters.Value<int?>("limit") ?? MaxLimit;
            if (limit < 1 || limit > MaxLimit) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to " + MaxLimit + ".");

            var candidates = doc.GetUnusedElements(new HashSet<ElementId>())
                .Select(doc.GetElement)
                .OfType<FamilySymbol>()
                .Where(s => s.Family == null || !s.Family.IsInPlace)
                .OrderBy(s => s.FamilyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var chosen = candidates.Take(limit).ToList();
            var items = new JArray(chosen.Select(s => new JObject
            {
                ["id"] = RevitCompat.GetId(s.Id),
                ["family"] = s.FamilyName,
                ["type"] = s.Name,
                ["category"] = s.Category?.Name
            }));

            if (dryRun)
                return CommandResult.Ok(new JObject
                {
                    ["dry_run"] = true,
                    ["unused_family_types"] = candidates.Count,
                    ["would_delete"] = chosen.Count,
                    ["truncated"] = candidates.Count > chosen.Count,
                    ["items"] = items
                });

            var deleted = new List<long>();
            var errors = new JArray();
            var gone = new HashSet<long>();
            using (var tx = new Transaction(doc, "B2M: Purge unused family types"))
            {
                tx.Start();
                foreach (var symbol in chosen)
                {
                    var id = RevitCompat.GetId(symbol.Id);
                    if (gone.Contains(id))
                    {
                        deleted.Add(id);
                        continue;
                    }
                    try
                    {
                        foreach (var removed in doc.Delete(symbol.Id)) gone.Add(RevitCompat.GetId(removed));
                        deleted.Add(id);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(new JObject { ["id"] = id, ["error"] = ex.Message });
                    }
                }
                if (tx.Commit() != TransactionStatus.Committed)
                    return CommandResult.Fail("NOT_COMMITTED: Revit rolled the purge back; see failures. Nothing was deleted.");
            }

            return CommandResult.Ok(new JObject
            {
                ["dry_run"] = false,
                ["unused_family_types"] = candidates.Count,
                ["deleted"] = deleted.Count,
                ["failedCount"] = errors.Count,
                ["deletedIds"] = new JArray(deleted),
                ["errors"] = errors,
                ["items"] = items
            });
        }
    }
}
