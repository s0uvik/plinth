// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Geometry/ComputeElementAreaHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: m2 = ft2 * 0.09290304; shared geometry walk; camelCase aliases dropped.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Geometry
{
    public sealed class ComputeElementAreaHandler : IRevitCommand
    {
        private const int MaxIds = 500;

        public string Name => "compute_element_area";
        public string Description => "Total solid face area (m2, all faces of the element's solids) of up to 500 elements, with the total.";
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
                int solids = 0, faces = 0;
                var squareFeet = 0.0;
                try
                {
                    GeometryHelpers.Walk(element.get_Geometry(options), obj =>
                    {
                        if (!(obj is Solid solid) || solid.Faces.Size == 0) return;
                        solids++;
                        foreach (Face face in solid.Faces)
                        {
                            if (face.Area <= 1e-9) continue;
                            faces++;
                            squareFeet += face.Area;
                        }
                    });
                }
                catch (Exception ex)
                {
                    failed.Add(new JObject { ["element_id"] = id, ["error"] = "GEOMETRY_FAILED: " + ex.Message });
                    continue;
                }
                var m2 = Math.Round(squareFeet * GeometryHelpers.M2PerFt2, 6);
                total += m2;
                var row = GeometryHelpers.Describe(element);
                row["solid_count"] = solids;
                row["face_count"] = faces;
                row["area_m2"] = m2;
                row["source"] = "solid_faces";
                row["limitations"] = faces == 0 ? new JArray("No solid faces found.") : new JArray();
                elements.Add(row);
            }

            return CommandResult.Ok(new JObject
            {
                ["unit"] = "m2",
                ["requested"] = ids.Count,
                ["returned"] = elements.Count,
                ["elements"] = elements,
                ["total_area_m2"] = Math.Round(total, 6),
                ["failed"] = failed
            });
        }
    }
}
