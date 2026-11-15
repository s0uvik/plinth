// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Families/ListLoadedFamiliesHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: ids are integers (system families carry their first type's id as type_id instead of a
// "system:<id>" string); category_filter also takes built-in names such as OST_Doors; instance counts come
// from one pass over the model instead of one collector per family; rows carry builtInCategory.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Families
{
    public sealed class ListLoadedFamiliesHandler : IRevitCommand
    {
        private static readonly Type[] SystemTypeClasses =
        {
            typeof(WallType), typeof(FloorType), typeof(CeilingType), typeof(RoofType),
            typeof(Autodesk.Revit.DB.Plumbing.PipeType), typeof(Autodesk.Revit.DB.Mechanical.DuctType),
            typeof(Autodesk.Revit.DB.Electrical.ConduitType), typeof(Autodesk.Revit.DB.Electrical.CableTrayType)
        };

        public string Name => "list_loaded_families";
        public string Description =>
            "List the families in the document: loadable, in-place and system (wall, floor, ceiling, roof, pipe, duct, conduit, cable tray), "
            + "with type count and optionally placed-instance count. category_filter matches a category display name (contains) or a built-in name such as OST_Doors.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""category_filter"":{""type"":""string""},
            ""kind_filter"":{""type"":""string"",""enum"":[""all"",""system"",""loadable"",""inplace""]},
            ""include_instance_count"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var filter = p.Value<string>("category_filter");
            var kind = p.Value<string>("kind_filter") ?? "all";
            var withCounts = p.Value<bool?>("include_instance_count") ?? false;
            var limit = p.Value<int?>("limit") ?? 1000;
            if (limit < 1 || limit > 10000) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to 10000.");

            Func<Category, bool> categoryMatches = c =>
            {
                if (string.IsNullOrEmpty(filter)) return true;
                if (c == null) return false;
                if (filter.StartsWith("OST_", StringComparison.OrdinalIgnoreCase))
                    return string.Equals(Lookup.BuiltInName(c), filter, StringComparison.OrdinalIgnoreCase);
                return c.Name != null && c.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
            };

            // Instances per type id, counted once.
            var perType = new Dictionary<long, int>();
            if (withCounts)
            {
                foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                {
                    var typeId = e.GetTypeId();
                    if (typeId == null || typeId == ElementId.InvalidElementId) continue;
                    var key = RevitCompat.GetId(typeId);
                    perType[key] = perType.TryGetValue(key, out var n) ? n + 1 : 1;
                }
            }
            Func<IEnumerable<ElementId>, JToken> count = ids =>
                withCounts ? (JToken)ids.Sum(id => perType.TryGetValue(RevitCompat.GetId(id), out var n) ? n : 0) : null;

            var rows = new List<JObject>();
            if (kind == "all" || kind == "loadable" || kind == "inplace")
            {
                foreach (var family in new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>())
                {
                    var thisKind = FamiliesHelpers.Kind(family);
                    if (kind != "all" && kind != thisKind) continue;
                    if (!categoryMatches(family.FamilyCategory)) continue;
                    var symbolIds = family.GetFamilySymbolIds();
                    rows.Add(new JObject
                    {
                        ["id"] = RevitCompat.GetId(family.Id),
                        ["type_id"] = null,
                        ["name"] = family.Name,
                        ["category"] = family.FamilyCategory?.Name,
                        ["builtInCategory"] = Lookup.BuiltInName(family.FamilyCategory),
                        ["kind"] = thisKind,
                        ["type_count"] = symbolIds.Count,
                        ["instance_count"] = count(symbolIds),
                        ["is_editable"] = family.IsEditable
                    });
                }
            }
            if (kind == "all" || kind == "system")
            {
                foreach (var typeClass in SystemTypeClasses)
                {
                    var groups = new FilteredElementCollector(doc).OfClass(typeClass).Cast<ElementType>()
                        .Where(t => categoryMatches(t.Category))
                        .GroupBy(t => (t.Category?.Name ?? "", t.FamilyName ?? ""));
                    foreach (var g in groups)
                    {
                        var first = g.OrderBy(t => RevitCompat.GetId(t.Id)).First();
                        rows.Add(new JObject
                        {
                            ["id"] = null,
                            ["type_id"] = RevitCompat.GetId(first.Id),
                            ["name"] = g.Key.Item2,
                            ["category"] = first.Category?.Name,
                            ["builtInCategory"] = Lookup.BuiltInName(first.Category),
                            ["kind"] = "system",
                            ["type_count"] = g.Count(),
                            ["instance_count"] = count(g.Select(t => t.Id)),
                            ["is_editable"] = false
                        });
                    }
                }
            }

            var ordered = rows.OrderBy(r => r.Value<string>("category") ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Value<string>("kind"))
                .ThenBy(r => r.Value<string>("name") ?? "", StringComparer.OrdinalIgnoreCase).ToList();
            return CommandResult.Ok(new JObject
            {
                ["doc_title"] = doc.Title,
                ["total_families"] = ordered.Count,
                ["returned_families"] = Math.Min(limit, ordered.Count),
                ["limit_hit"] = ordered.Count > limit,
                ["families"] = new JArray(ordered.Take(limit))
            });
        }
    }
}
