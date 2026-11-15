// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Geometry/GetElementBoundingBoxHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: mm through Mm (0.1 mm); a view that is a template is refused; camelCase aliases
// dropped (the schema rejects unknown fields); duplicate ids are counted once.

using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Geometry
{
    using View = Autodesk.Revit.DB.View;

    public sealed class GetElementBoundingBoxHandler : IRevitCommand
    {
        private const int MaxIds = 500;

        public string Name => "get_element_bounding_box";
        public string Description => "Bounding box (min, max, center, size in mm) of up to 500 elements, model-wide or as seen in a view; optionally the box transform.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""view_id"":{""type"":""integer""},
            ""include_transform"":{""type"":""boolean""}},""required"":[""element_ids""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!GeometryHelpers.TryIds(p, "element_ids", MaxIds, out var ids, out var failure)) return failure;
            if (!GeometryHelpers.TryOptionalView(doc, p.Value<long?>("view_id"), out View view, out failure)) return failure;
            var includeTransform = p.Value<bool?>("include_transform") ?? false;

            var boxes = new JArray();
            var failed = new JArray();
            foreach (var id in ids)
            {
                var element = doc.GetElement(RevitCompat.ToElementId(id));
                if (element == null)
                {
                    failed.Add(new JObject { ["element_id"] = id, ["error"] = "ELEMENT_NOT_FOUND" });
                    continue;
                }
                var box = element.get_BoundingBox(view);
                if (box == null)
                {
                    failed.Add(new JObject { ["element_id"] = id, ["error"] = "NO_BOUNDING_BOX: the element has no bounding box" + (view == null ? "." : " in this view.") });
                    continue;
                }
                var row = GeometryHelpers.Describe(element);
                row["unique_id"] = element.UniqueId;
                foreach (var prop in GeometryHelpers.FullBox(box).Properties()) row[prop.Name] = prop.Value;
                if (includeTransform && box.Transform != null)
                {
                    row["transform"] = new JObject
                    {
                        ["origin"] = GeometryHelpers.Point(box.Transform.Origin),
                        ["basis_x"] = GeometryHelpers.Vector(box.Transform.BasisX),
                        ["basis_y"] = GeometryHelpers.Vector(box.Transform.BasisY),
                        ["basis_z"] = GeometryHelpers.Vector(box.Transform.BasisZ)
                    };
                }
                boxes.Add(row);
            }

            return CommandResult.Ok(new JObject
            {
                ["unit"] = "mm",
                ["requested"] = ids.Count,
                ["returned"] = boxes.Count,
                ["view_id"] = view == null ? null : (JToken)RevitCompat.GetId(view.Id),
                ["view_name"] = view?.Name,
                ["boxes"] = boxes,
                ["failed"] = failed
            });
        }
    }
}
