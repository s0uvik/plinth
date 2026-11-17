// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Geometry/AnalyzeGeometryComplexityHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: a separate max_scan (default 2000, up to 20000) bounds how many elements are
// analysed, and limit (1-500) only bounds how many ranked rows come back (rvt-mcp used limit for
// both, so the ranking only covered the first few elements); missing element ids and unknown
// categories are reported, not skipped silently; same score formula.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Geometry
{
    using View = Autodesk.Revit.DB.View;

    public sealed class AnalyzeGeometryComplexityHandler : IRevitCommand
    {
        private const int MaxIds = 5000;
        private const int MaxScan = 20000;

        public string Name => "analyze_geometry_complexity";
        public string Description =>
            "Rank elements by geometric complexity (score = faces + edges + mesh vertices + 5*solids + 10*instances) to find heavy families. "
            + "Scope with element_ids, categories and/or view_id (at least one). max_scan bounds elements analysed (default 2000), limit bounds rows returned (default 200).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""categories"":{""type"":""array"",""items"":{""type"":""string""}},
            ""view_id"":{""type"":""integer""},
            ""detail_level"":{""type"":""string"",""enum"":[""Coarse"",""Medium"",""Fine""]},
            ""max_scan"":{""type"":""integer""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        private sealed class Row
        {
            public Element Element;
            public int Solids, Faces, Edges, Meshes, MeshVertices, Curves, Instances;
            public int Score => Faces + Edges + MeshVertices + Solids * 5 + Instances * 10;
        }

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            var idsGiven = (p["element_ids"] as JArray)?.Count > 0;
            var categoriesGiven = (p["categories"] as JArray)?.Count > 0;
            var viewId = p.Value<long?>("view_id");
            if (!idsGiven && !categoriesGiven && viewId == null)
                return CommandResult.Fail("INVALID_PARAMS: give element_ids, categories or view_id to scope the analysis (a whole-model scan is refused).");
            var limit = p.Value<int?>("limit") ?? 200;
            if (limit < 1 || limit > 500) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to 500.");
            var maxScan = p.Value<int?>("max_scan") ?? 2000;
            if (maxScan < 1 || maxScan > MaxScan) return CommandResult.Fail("INVALID_PARAMS: max_scan must be 1 to " + MaxScan + ".");
            if (!GeometryHelpers.TryDetailLevel(p.Value<string>("detail_level"), out var level, out var failure)) return failure;
            if (!GeometryHelpers.TryOptionalView(doc, viewId, out View view, out failure)) return failure;
            List<Category> categories = null;
            if (categoriesGiven && !GeometryHelpers.TryCategories(doc, p["categories"].Values<string>(), out categories, out failure)) return failure;

            var failed = new JArray();
            IEnumerable<Element> scope;
            if (idsGiven)
            {
                if (!GeometryHelpers.TryIds(p, "element_ids", MaxIds, out var ids, out failure)) return failure;
                var visible = view == null ? null : new HashSet<ElementId>(new FilteredElementCollector(doc, view.Id).ToElementIds());
                var found = new List<Element>();
                foreach (var id in ids)
                {
                    var element = doc.GetElement(RevitCompat.ToElementId(id));
                    if (element == null) failed.Add(new JObject { ["element_id"] = id, ["error"] = "ELEMENT_NOT_FOUND" });
                    else if (categories != null && (element.Category == null || categories.All(c => c.Id != element.Category.Id))) continue;
                    else if (visible != null && !visible.Contains(element.Id)) continue;
                    else found.Add(element);
                }
                scope = found;
            }
            else
            {
                var collector = GeometryHelpers.Collector(doc, view).WhereElementIsNotElementType();
                if (categories != null) collector.WherePasses(new ElementMulticategoryFilter(categories.ConvertAll(c => c.Id)));
                scope = collector;
            }

            var options = GeometryHelpers.GeometryOptions(level);
            var rows = new List<Row>();
            var scanned = 0;
            var truncated = false;
            foreach (var element in scope)
            {
                if (scanned >= maxScan)
                {
                    truncated = true;
                    break;
                }
                scanned++;
                var row = new Row { Element = element };
                try
                {
                    GeometryHelpers.Walk(element.get_Geometry(options), obj =>
                    {
                        switch (obj)
                        {
                            case Solid solid:
                                row.Solids++;
                                row.Faces += solid.Faces.Size;
                                row.Edges += solid.Edges.Size;
                                break;
                            case Mesh mesh:
                                row.Meshes++;
                                row.MeshVertices += mesh.Vertices.Count;
                                break;
                            case Curve _:
                                row.Curves++;
                                break;
                        }
                    }, _ => row.Instances++);
                }
                catch (Exception ex)
                {
                    if (failed.Count < 200)
                        failed.Add(new JObject { ["element_id"] = RevitCompat.GetId(element.Id), ["error"] = "GEOMETRY_FAILED: " + ex.Message });
                    continue;
                }
                rows.Add(row);
            }

            var ranked = rows.OrderByDescending(r => r.Score).ToList();
            var rank = 0;
            var elements = new JArray(ranked.Take(limit).Select(r =>
            {
                var o = GeometryHelpers.Describe(r.Element);
                o["solid_count"] = r.Solids;
                o["face_count"] = r.Faces;
                o["edge_count"] = r.Edges;
                o["mesh_count"] = r.Meshes;
                o["mesh_vertex_count"] = r.MeshVertices;
                o["curve_count"] = r.Curves;
                o["geometry_instance_count"] = r.Instances;
                o["complexity_score"] = r.Score;
                o["rank"] = ++rank;
                return o;
            }));

            return CommandResult.Ok(new JObject
            {
                ["detail_level"] = level.ToString(),
                ["scanned"] = scanned,
                ["analysed"] = rows.Count,
                ["returned"] = elements.Count,
                ["limit"] = limit,
                ["scan_truncated"] = truncated,
                ["rows_truncated"] = ranked.Count > elements.Count,
                ["summary"] = new JObject
                {
                    ["total_solids"] = rows.Sum(r => r.Solids),
                    ["total_faces"] = rows.Sum(r => r.Faces),
                    ["total_edges"] = rows.Sum(r => r.Edges),
                    ["top_complexity_score"] = ranked.Count > 0 ? ranked[0].Score : 0
                },
                ["elements"] = elements,
                ["failed"] = failed
            });
        }
    }
}
