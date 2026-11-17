// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Geometry/GetElementCentroidHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: mm through Mm, m3 = ft3 * 0.0283168466; shared solid walk; aliases dropped.
// The bounding-box fallback is explicit in the result (strategy, fallback_used), as in rvt-mcp.

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Geometry
{
    public sealed class GetElementCentroidHandler : IRevitCommand
    {
        private const int MaxIds = 500;

        public string Name => "get_element_centroid";
        public string Description =>
            "Centroid (mm) of up to 500 elements: 'solid_then_bbox' (volume-weighted solid centroid, bounding-box centre when there is no solid; default) or 'bbox'.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""strategy"":{""type"":""string"",""enum"":[""bbox"",""solid_then_bbox""]}},""required"":[""element_ids""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!GeometryHelpers.TryIds(p, "element_ids", MaxIds, out var ids, out var failure)) return failure;
            var trySolid = (p.Value<string>("strategy") ?? "solid_then_bbox") == "solid_then_bbox";
            var options = GeometryHelpers.GeometryOptions(ViewDetailLevel.Medium);

            var centroids = new JArray();
            var failed = new JArray();
            foreach (var id in ids)
            {
                var element = doc.GetElement(RevitCompat.ToElementId(id));
                if (element == null)
                {
                    failed.Add(new JObject { ["element_id"] = id, ["error"] = "ELEMENT_NOT_FOUND" });
                    continue;
                }

                XYZ centroid = null;
                double? volume = null;
                var limitations = new JArray();
                if (trySolid)
                {
                    string why = null;
                    try
                    {
                        if (TrySolidCentroid(GeometryHelpers.Solids(element, options), out centroid, out var cubicFeet)) volume = cubicFeet;
                        else why = "no solid with volume and a computable centroid.";
                    }
                    catch (Exception ex)
                    {
                        why = ex.Message;
                    }
                    if (why != null) limitations.Add("Solid centroid unavailable: " + why);
                }
                var strategy = "solid";
                if (centroid == null)
                {
                    var box = element.get_BoundingBox(null);
                    if (box == null)
                    {
                        failed.Add(new JObject { ["element_id"] = id, ["error"] = "NO_GEOMETRY: the element has neither solids nor a bounding box." });
                        continue;
                    }
                    centroid = 0.5 * (box.Min + box.Max);
                    strategy = "bbox";
                }

                var row = GeometryHelpers.Describe(element);
                row["strategy"] = strategy;
                row["centroid"] = GeometryHelpers.Point(centroid);
                row["volume_m3"] = volume.HasValue ? (JToken)Math.Round(volume.Value * GeometryHelpers.M3PerFt3, 6) : null;
                row["fallback_used"] = trySolid && strategy == "bbox";
                row["limitations"] = limitations;
                centroids.Add(row);
            }

            return CommandResult.Ok(new JObject
            {
                ["unit"] = "mm",
                ["requested"] = ids.Count,
                ["returned"] = centroids.Count,
                ["centroids"] = centroids,
                ["failed"] = failed
            });
        }

        private static bool TrySolidCentroid(List<Solid> solids, out XYZ centroid, out double volume)
        {
            centroid = null;
            volume = 0;
            var sum = XYZ.Zero;
            foreach (var solid in solids)
            {
                XYZ c;
                try
                {
                    c = solid.ComputeCentroid();
                }
                catch
                {
                    continue; // A solid whose centroid Revit cannot compute is left out of the weighting.
                }
                if (c == null) continue;
                sum += c * solid.Volume;
                volume += solid.Volume;
            }
            if (volume <= 1e-9) return false;
            centroid = sum / volume;
            return true;
        }
    }
}
