// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Families/AuditFamiliesHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: instance counts come from one pass over all family instances (rvt-mcp ran one collector per
// category); ids are integers; ResponsePaging is replaced by local paging with the same start_index /
// limit_per_section fields; "unused" says it means "no placed instances" (a profile used by a sweep counts as unused).

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Families
{
    public sealed class AuditFamiliesHandler : IRevitCommand
    {
        public string Name => "audit_families";
        public string Description =>
            "Audit loadable and in-place families: in-place families, families with no placed instances (profiles and other "
            + "non-placed uses also count as unused), duplicate names, and families with many types. Returns recommendations. Read-only.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""include_unused"":{""type"":""boolean""},
            ""include_inplace"":{""type"":""boolean""},
            ""include_duplicate_names"":{""type"":""boolean""},
            ""include_high_type_count"":{""type"":""boolean""},
            ""high_type_count_threshold"":{""type"":""integer""},
            ""start_index"":{""type"":""integer""},
            ""limit_per_section"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var threshold = p.Value<int?>("high_type_count_threshold") ?? 20;
            var start = p.Value<int?>("start_index") ?? 0;
            var size = p.Value<int?>("limit_per_section") ?? 100;
            if (threshold < 1) return CommandResult.Fail("INVALID_PARAMS: high_type_count_threshold must be at least 1.");
            if (start < 0) return CommandResult.Fail("INVALID_PARAMS: start_index must not be negative.");
            if (size < 1 || size > 500) return CommandResult.Fail("INVALID_PARAMS: limit_per_section must be 1 to 500.");

            var families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var symbolToFamily = new Dictionary<long, long>();
            var typeCounts = new Dictionary<long, int>();
            foreach (var f in families)
            {
                var ids = f.GetFamilySymbolIds();
                typeCounts[RevitCompat.GetId(f.Id)] = ids.Count;
                foreach (var id in ids) symbolToFamily[RevitCompat.GetId(id)] = RevitCompat.GetId(f.Id);
            }
            var instanceCounts = new Dictionary<long, int>();
            foreach (var e in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType())
            {
                if (!symbolToFamily.TryGetValue(RevitCompat.GetId(e.GetTypeId()), out var famId)) continue;
                instanceCounts[famId] = instanceCounts.TryGetValue(famId, out var n) ? n + 1 : 1;
            }

            Func<Family, JObject> row = f => new JObject
            {
                ["id"] = RevitCompat.GetId(f.Id),
                ["name"] = f.Name,
                ["category"] = f.FamilyCategory?.Name,
                ["builtInCategory"] = Lookup.BuiltInName(f.FamilyCategory)
            };
            var recommendations = new List<string>();
            var sections = new JObject();
            var counts = new JObject();
            var truncated = false;
            int? nextIndex = null;
            Action<string, IList<JToken>> add = (key, items) =>
            {
                var meta = FamiliesHelpers.Page(items, start, size, t => t, out var page);
                sections[key] = page;
                counts[key] = items.Count;
                if (meta.Value<bool>("truncated"))
                {
                    truncated = true;
                    nextIndex = Math.Max(nextIndex ?? 0, meta.Value<int>("next_index"));
                }
            };

            if (p.Value<bool?>("include_inplace") ?? true)
            {
                var list = families.Where(f => f.IsInPlace).ToList();
                foreach (var f in list) recommendations.Add("In-place family '" + f.Name + "' (" + f.FamilyCategory?.Name + "): consider a loadable family for reuse and a lighter model.");
                add("inplace_families", list.Select(f => (JToken)row(f)).ToList());
            }
            if (p.Value<bool?>("include_unused") ?? true)
            {
                var list = families.Where(f => !instanceCounts.ContainsKey(RevitCompat.GetId(f.Id))).ToList();
                foreach (var f in list) recommendations.Add("Family '" + f.Name + "' has no placed instances: consider purging (check it is not used as a profile or nested type first).");
                add("unused_families", list.Select(f => (JToken)row(f)).ToList());
            }
            if (p.Value<bool?>("include_high_type_count") ?? true)
            {
                var list = families.Where(f => typeCounts[RevitCompat.GetId(f.Id)] >= threshold)
                    .OrderByDescending(f => typeCounts[RevitCompat.GetId(f.Id)]).ToList();
                foreach (var f in list) recommendations.Add("Family '" + f.Name + "' has " + typeCounts[RevitCompat.GetId(f.Id)] + " types (threshold " + threshold + "): review for unused types.");
                add("high_type_count_families", list.Select(f =>
                {
                    var o = row(f);
                    o["type_count"] = typeCounts[RevitCompat.GetId(f.Id)];
                    return (JToken)o;
                }).ToList());
            }
            if (p.Value<bool?>("include_duplicate_names") ?? true)
            {
                var groups = families.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
                foreach (var g in groups) recommendations.Add("Family name '" + g.Key + "' is used " + g.Count() + " times: rename for clarity.");
                add("duplicate_name_groups", groups.Select(g => (JToken)new JObject
                {
                    ["name"] = g.Key,
                    ["families"] = new JArray(g.Select(row))
                }).ToList());
            }
            if (recommendations.Count == 0) recommendations.Add("No family-level issues found with the chosen checks.");
            add("recommendations", recommendations.Select(r => (JToken)r).ToList());

            var result = new JObject
            {
                ["doc_title"] = doc.Title,
                ["total_families_scanned"] = families.Count,
                ["start_index"] = start,
                ["limit_per_section"] = size,
                ["section_counts"] = counts,
                ["truncated"] = truncated,
                ["next_index"] = nextIndex
            };
            foreach (var prop in sections.Properties()) result[prop.Name] = prop.Value;
            return CommandResult.Ok(result);
        }
    }
}
