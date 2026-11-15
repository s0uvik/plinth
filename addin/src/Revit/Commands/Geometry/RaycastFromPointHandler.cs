// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Geometry/RaycastFromPointHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: mm through Mm; categories by built-in name or display name, a miss lists the
// valid names; camelCase aliases dropped.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Geometry
{
    using View = Autodesk.Revit.DB.View;

    public sealed class RaycastFromPointHandler : IRevitCommand
    {
        public string Name => "raycast_from_point";
        public string Description =>
            "Cast a ray from a point (mm) along a direction in a 3D view and return the nearest element hit, its hit point and distance (mm). "
            + "Optional category filter (OST_ names or display names); max_distance in mm (default 100000).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""x"":{""type"":""number""},""y"":{""type"":""number""},""z"":{""type"":""number""},
            ""dir_x"":{""type"":""number""},""dir_y"":{""type"":""number""},""dir_z"":{""type"":""number""},
            ""view_3d_id"":{""type"":""integer""},
            ""categories"":{""type"":""array"",""items"":{""type"":""string""}},
            ""max_distance"":{""type"":""number""}},
            ""required"":[""x"",""y"",""z"",""dir_x"",""dir_y"",""dir_z"",""view_3d_id""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var viewId = p.Value<long>("view_3d_id");
            if (!Lookup.TryView(doc, viewId, out View view, out var failure)) return failure;
            if (!(view is View3D view3D))
                return CommandResult.Fail("NOT_A_3D_VIEW: view " + viewId + " ('" + view.Name + "') is not a 3D view.");

            var direction = new XYZ(p.Value<double>("dir_x"), p.Value<double>("dir_y"), p.Value<double>("dir_z"));
            if (direction.GetLength() < 1e-9) return CommandResult.Fail("INVALID_PARAMS: the direction vector must not be zero.");
            direction = direction.Normalize();
            var maxMm = p.Value<double?>("max_distance") ?? 100000.0;
            if (maxMm <= 0) return CommandResult.Fail("INVALID_PARAMS: max_distance must be positive (mm).");

            ReferenceIntersector intersector;
            if (p["categories"] is JArray names && names.Count > 0)
            {
                if (!GeometryHelpers.TryCategories(doc, names.Values<string>(), out var categories, out failure)) return failure;
                intersector = new ReferenceIntersector(new ElementMulticategoryFilter(categories.ConvertAll(c => c.Id)), FindReferenceTarget.Element, view3D);
            }
            else
            {
                intersector = new ReferenceIntersector(view3D);
            }

            var origin = new XYZ(Mm.ToFeet(p.Value<double>("x")), Mm.ToFeet(p.Value<double>("y")), Mm.ToFeet(p.Value<double>("z")));
            ReferenceWithContext nearest;
            try
            {
                nearest = intersector.FindNearest(origin, direction);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("RAYCAST_FAILED: " + ex.Message);
            }

            var result = new JObject
            {
                ["unit"] = "mm",
                ["origin"] = GeometryHelpers.Point(origin),
                ["direction"] = GeometryHelpers.Vector(direction),
                ["view_3d_id"] = viewId,
                ["hit"] = false
            };
            if (nearest == null || nearest.Proximity > Mm.ToFeet(maxMm)) return CommandResult.Ok(result);

            var reference = nearest.GetReference();
            var hit = doc.GetElement(reference.ElementId);
            result["hit"] = true;
            result["hit_element"] = hit == null ? null : GeometryHelpers.Describe(hit);
            result["proximity"] = Mm.FromFeet(nearest.Proximity);
            result["global_point"] = GeometryHelpers.Point(reference.GlobalPoint);
            result["reference_stable_representation"] = reference.ConvertToStableRepresentation(doc);
            return CommandResult.Ok(result);
        }
    }
}
