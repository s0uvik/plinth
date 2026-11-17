// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Geometry/ProjectPointOntoFaceHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: mm through Mm; a point that does not project is a NOT_PROJECTED error (rvt-mcp
// returned success with an error field); the face count is returned with out-of-range indexes.

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Geometry
{
    public sealed class ProjectPointOntoFaceHandler : IRevitCommand
    {
        public string Name => "project_point_onto_face";
        public string Description =>
            "Project a point (mm) onto one face of an element's solids (face_index, 0-based, default 0) and return the projected point, "
            + "distance (mm), UV and normal.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_id"":{""type"":""integer""},
            ""x"":{""type"":""number""},""y"":{""type"":""number""},""z"":{""type"":""number""},
            ""face_index"":{""type"":""integer""},
            ""detail_level"":{""type"":""string"",""enum"":[""Coarse"",""Medium"",""Fine""]}},
            ""required"":[""element_id"",""x"",""y"",""z""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            var id = p.Value<long>("element_id");
            if (!Lookup.TryElement(doc, id, "model element", out Element element, out var failure)) return failure;
            if (!GeometryHelpers.TryDetailLevel(p.Value<string>("detail_level"), out var level, out failure)) return failure;
            var faceIndex = p.Value<int?>("face_index") ?? 0;
            if (faceIndex < 0) return CommandResult.Fail("INVALID_PARAMS: face_index must be 0 or more.");

            var faces = new List<Face>();
            foreach (var solid in GeometryHelpers.Solids(element, GeometryHelpers.GeometryOptions(level)))
                foreach (Face face in solid.Faces) faces.Add(face);
            if (faces.Count == 0) return CommandResult.Fail("NO_FACES: element " + id + " has no solid faces at this detail level.");
            if (faceIndex >= faces.Count)
                return CommandResult.Fail("INVALID_PARAMS: face_index " + faceIndex + " is out of range; the element has " + faces.Count + " faces (0 to " + (faces.Count - 1) + ").",
                    new JObject { ["face_count"] = faces.Count });

            var face0 = faces[faceIndex];
            var point = new XYZ(Mm.ToFeet(p.Value<double>("x")), Mm.ToFeet(p.Value<double>("y")), Mm.ToFeet(p.Value<double>("z")));
            IntersectionResult projection;
            try
            {
                projection = face0.Project(point);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("PROJECTION_FAILED: " + ex.Message);
            }
            if (projection == null)
                return CommandResult.Fail("NOT_PROJECTED: the point does not project onto face " + faceIndex + " (it lies outside the face's bounds); try another face_index.",
                    new JObject { ["face_count"] = faces.Count });

            var uv = projection.UVPoint;
            return CommandResult.Ok(new JObject
            {
                ["unit"] = "mm",
                ["element_id"] = id,
                ["input_point"] = GeometryHelpers.Point(point),
                ["face_index"] = faceIndex,
                ["face_count"] = faces.Count,
                ["projected_point"] = GeometryHelpers.Point(projection.XYZPoint),
                ["distance"] = Mm.FromFeet(point.DistanceTo(projection.XYZPoint)),
                ["uv"] = uv == null ? null : new JObject { ["u"] = Math.Round(uv.U, 6), ["v"] = Math.Round(uv.V, 6) },
                ["normal"] = uv == null ? null : GeometryHelpers.Vector(face0.ComputeNormal(uv))
            });
        }
    }
}
