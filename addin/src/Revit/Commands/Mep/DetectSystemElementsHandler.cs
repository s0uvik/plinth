// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Mep/DetectSystemElementsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: the connector walk is capped (max_traversal, default 5000, hard 20000) and reports
// traversal_truncated (rvt-mcp walked without bound); elements are grouped by every built-in category met
// (OST_PipeCurves, OST_DuctCurves, OST_CableTray...) instead of only four piping buckets; errors are "CODE: message".

using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Mep
{
    public sealed class DetectSystemElementsHandler : IRevitCommand
    {
        private const int DefaultTraversal = 5000;
        private const int HardTraversal = 20000;

        public string Name => "detect_system_elements";
        public string Description =>
            "Walk the connector graph from one MEP element (pipe, duct, fitting, equipment...) and return the connected elements: "
            + "total count, bounding box in mm, and per built-in category a count plus a page of element IDs "
            + "(start_element, max_elements per category). The walk stops after max_traversal elements (default 5000, max 20000).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_id"":{""type"":""integer""},
            ""start_element"":{""type"":""integer""},
            ""max_elements"":{""type"":""integer""},
            ""max_traversal"":{""type"":""integer""}},
            ""required"":[""element_id""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var startElement = p.Value<int?>("start_element") ?? 0;
            var maxElements = p.Value<int?>("max_elements") ?? 500;
            var maxTraversal = p.Value<int?>("max_traversal") ?? DefaultTraversal;
            if (startElement < 0) return CommandResult.Fail("INVALID_PARAMS: start_element must be 0 or more.");
            if (maxElements < 1 || maxElements > 5000) return CommandResult.Fail("INVALID_PARAMS: max_elements must be between 1 and 5000.");
            if (maxTraversal < 1 || maxTraversal > HardTraversal)
                return CommandResult.Fail("INVALID_PARAMS: max_traversal must be between 1 and " + HardTraversal + ".");

            var id = p.Value<long>("element_id");
            var seed = doc.GetElement(RevitCompat.ToElementId(id));
            if (seed == null) return CommandResult.Fail("ELEMENT_NOT_FOUND: no element has id " + id + ".");
            if (MepHelpers.GetConnectorManager(seed) == null)
                return CommandResult.Fail("NO_CONNECTORS: element " + id + " has no MEP connectors.");

            // Breadth-first walk over physical connections, bounded.
            var visited = new HashSet<long> { RevitCompat.GetId(seed.Id) };
            var order = new List<Element> { seed };
            var queue = new Queue<Element>();
            queue.Enqueue(seed);
            string systemName = null;
            var truncated = false;
            while (queue.Count > 0 && !truncated)
            {
                var manager = MepHelpers.GetConnectorManager(queue.Dequeue());
                if (manager == null) continue;
                foreach (Connector connector in manager.Connectors)
                {
                    if (systemName == null)
                    {
                        try { systemName = connector.MEPSystem?.Name; } catch { /* electrical and logical connectors may throw */ }
                    }
                    ConnectorSet refs;
                    try { refs = connector.AllRefs; } catch { continue; }
                    foreach (Connector other in refs)
                    {
                        var owner = other.Owner;
                        if (owner == null) continue;
                        var ownerId = RevitCompat.GetId(owner.Id);
                        if (ownerId <= 0 || !visited.Add(ownerId)) continue; // Skip logical pseudo-owners and repeats.
                        if (visited.Count > maxTraversal)
                        {
                            visited.Remove(ownerId);
                            truncated = true;
                            break;
                        }
                        order.Add(owner);
                        queue.Enqueue(owner);
                    }
                    if (truncated) break;
                }
            }

            var groups = new SortedDictionary<string, List<long>>(System.StringComparer.Ordinal);
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            var hasBounds = false;
            foreach (var element in order)
            {
                var key = Lookup.BuiltInName(element.Category) ?? "uncategorized";
                if (!groups.TryGetValue(key, out var ids)) groups[key] = ids = new List<long>();
                ids.Add(RevitCompat.GetId(element.Id));

                BoundingBoxXYZ box = null;
                try { box = element.get_BoundingBox(null); } catch { /* some elements have no model geometry */ }
                if (box == null) continue;
                hasBounds = true;
                minX = System.Math.Min(minX, box.Min.X); minY = System.Math.Min(minY, box.Min.Y); minZ = System.Math.Min(minZ, box.Min.Z);
                maxX = System.Math.Max(maxX, box.Max.X); maxY = System.Math.Max(maxY, box.Max.Y); maxZ = System.Math.Max(maxZ, box.Max.Z);
            }

            var byCategory = new JObject();
            var pageTruncated = false;
            var returned = 0;
            foreach (var group in groups)
            {
                var page = group.Value.OrderBy(x => x).Skip(startElement).Take(maxElements).ToList();
                returned += page.Count;
                if (group.Value.Count > startElement + page.Count) pageTruncated = true;
                byCategory[group.Key] = new JObject { ["count"] = group.Value.Count, ["element_ids"] = new JArray(page) };
            }

            return CommandResult.Ok(new
            {
                seed_element_id = id,
                system_name = systemName,
                element_count = order.Count,
                traversal_truncated = truncated,
                max_traversal = maxTraversal,
                start_element = startElement,
                max_elements_per_category = maxElements,
                returned_element_id_count = returned,
                page_truncated = pageTruncated,
                next_element = pageTruncated ? (int?)(startElement + maxElements) : null,
                bounding_box_mm = hasBounds
                    ? new { min = new[] { Mm.FromFeet(minX), Mm.FromFeet(minY), Mm.FromFeet(minZ) }, max = new[] { Mm.FromFeet(maxX), Mm.FromFeet(maxY), Mm.FromFeet(maxZ) } }
                    : null,
                by_category = byCategory
            });
        }
    }
}
