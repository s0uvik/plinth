// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Geometry/GetElementGeometryHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: mm through Mm; one shared geometry walk; per-element geometry failures are
// reported instead of swallowed; vertex samples stay capped at 50 per element and 100 elements.

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Geometry
{
    public sealed class GetElementGeometryHandler : IRevitCommand
    {
        private const int MaxIds = 100;
        private const int MaxSamples = 50;

        public string Name => "get_element_geometry";
        public string Description => "Geometry summary of up to 100 elements: bounding box, counts of solids, faces, edges, curves, meshes and instances, optional vertex samples (mm, at most 50 each).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""detail_level"":{""type"":""string"",""enum"":[""Coarse"",""Medium"",""Fine""]},
            ""include_samples"":{""type"":""boolean""},
            ""sample_limit"":{""type"":""integer""}},""required"":[""element_ids""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!GeometryHelpers.TryIds(p, "element_ids", MaxIds, out var ids, out var failure)) return failure;
            if (!GeometryHelpers.TryDetailLevel(p.Value<string>("detail_level"), out var level, out failure)) return failure;
            var includeSamples = p.Value<bool?>("include_samples") ?? false;
            var sampleLimit = p.Value<int?>("sample_limit") ?? 20;
            if (sampleLimit < 0 || sampleLimit > MaxSamples) return CommandResult.Fail("INVALID_PARAMS: sample_limit must be 0 to " + MaxSamples + ".");
            var options = GeometryHelpers.GeometryOptions(level);

            var results = new JArray();
            var failed = new JArray();
            foreach (var id in ids)
            {
                var element = doc.GetElement(RevitCompat.ToElementId(id));
                if (element == null)
                {
                    failed.Add(new JObject { ["element_id"] = id, ["error"] = "ELEMENT_NOT_FOUND" });
                    continue;
                }

                int solids = 0, nonEmpty = 0, faces = 0, edges = 0, curves = 0, meshes = 0, instances = 0;
                var samples = new JArray();
                void Sample(IEnumerable<XYZ> points)
                {
                    if (!includeSamples || points == null) return;
                    foreach (var pt in points)
                    {
                        if (samples.Count >= sampleLimit) return;
                        samples.Add(GeometryHelpers.Point(pt));
                    }
                }

                GeometryElement geometry;
                try
                {
                    geometry = element.get_Geometry(options);
                    GeometryHelpers.Walk(geometry, obj =>
                    {
                        switch (obj)
                        {
                            case Solid solid:
                                solids++;
                                if (solid.Volume > 1e-9) nonEmpty++;
                                faces += solid.Faces.Size;
                                edges += solid.Edges.Size;
                                if (includeSamples)
                                    foreach (Edge edge in solid.Edges)
                                    {
                                        if (samples.Count >= sampleLimit) break;
                                        Sample(edge.Tessellate());
                                    }
                                break;
                            case Mesh mesh:
                                meshes++;
                                Sample(mesh.Vertices);
                                break;
                            case Curve curve:
                                curves++;
                                Sample(curve.Tessellate());
                                break;
                        }
                    }, _ => instances++);
                }
                catch (Exception ex)
                {
                    failed.Add(new JObject { ["element_id"] = id, ["error"] = "GEOMETRY_FAILED: " + ex.Message });
                    continue;
                }

                var row = GeometryHelpers.Describe(element);
                row["bounding_box"] = GeometryHelpers.FullBox(element.get_BoundingBox(null));
                row["geometry"] = new JObject
                {
                    ["solid_count"] = solids,
                    ["non_empty_solid_count"] = nonEmpty,
                    ["face_count"] = faces,
                    ["edge_count"] = edges,
                    ["curve_count"] = curves,
                    ["mesh_count"] = meshes,
                    ["instance_count"] = instances,
                    ["vertex_sample_count"] = samples.Count,
                    ["vertex_samples"] = includeSamples ? samples : null
                };
                if (geometry == null) row["limitations"] = new JArray("No geometry at this detail level.");
                results.Add(row);
            }

            return CommandResult.Ok(new JObject
            {
                ["unit"] = "mm",
                ["requested"] = ids.Count,
                ["returned"] = results.Count,
                ["detail_level"] = level.ToString(),
                ["results"] = results,
                ["failed"] = failed
            });
        }
    }
}
