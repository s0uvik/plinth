// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Geometry/ComputeElementVolumeHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: m3 = ft3 * 0.0283168466; shared solid walk; camelCase aliases dropped.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Geometry
{
    public sealed class ComputeElementVolumeHandler : IRevitCommand
    {
        private const int MaxIds = 500;

        public string Name => "compute_element_volume";
        public string Description => "Solid geometry volume (m3) of up to 500 elements, with the total.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""detail_level"":{""type"":""string"",""enum"":[""Coarse"",""Medium"",""Fine""]}},""required"":[""element_ids""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!GeometryHelpers.TryIds(p, "element_ids", MaxIds, out var ids, out var failure)) return failure;
            if (!GeometryHelpers.TryDetailLevel(p.Value<string>("detail_level"), out var level, out failure)) return failure;
            var options = GeometryHelpers.GeometryOptions(level);

            var elements = new JArray();
            var failed = new JArray();
            var total = 0.0;
            foreach (var id in ids)
            {
                var element = doc.GetElement(RevitCompat.ToElementId(id));
                if (element == null)
                {
                    failed.Add(new JObject { ["element_id"] = id, ["error"] = "ELEMENT_NOT_FOUND" });
                    continue;
                }
                double cubicFeet;
                int solidCount;
                try
                {
                    var solids = GeometryHelpers.Solids(element, options);
                    solidCount = solids.Count;
                    cubicFeet = solids.Sum(s => s.Volume);
                }
                catch (Exception ex)
                {
                    failed.Add(new JObject { ["element_id"] = id, ["error"] = "GEOMETRY_FAILED: " + ex.Message });
                    continue;
                }
                var m3 = Math.Round(cubicFeet * GeometryHelpers.M3PerFt3, 6);
                total += m3;
                var row = GeometryHelpers.Describe(element);
                row["solid_count"] = solidCount;
                row["volume_m3"] = m3;
                row["source"] = "solid_geometry";
                row["limitations"] = solidCount == 0 ? new JArray("No solid geometry with volume found.") : new JArray();
                elements.Add(row);
            }

            return CommandResult.Ok(new JObject
            {
                ["unit"] = "m3",
                ["requested"] = ids.Count,
                ["returned"] = elements.Count,
                ["elements"] = elements,
                ["total_volume_m3"] = Math.Round(total, 6),
                ["failed"] = failed
            });
        }
    }
}
