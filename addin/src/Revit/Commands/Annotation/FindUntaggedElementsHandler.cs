// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Annotations/FindUntaggedElementsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: a category is matched exactly by name or built-in name (rvt-mcp also took any OST_ name ending
// with the input) and a miss lists close matches; tagged ids come from GetTaggedLocalElementIds, RoomTag.Room and
// AreaTag.Area directly (no reflection); unplaced rooms are left out; limit must be 1 to 500.

using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Annotation
{
    // B2M.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class FindUntaggedElementsHandler : IRevitCommand
    {
        public string Name => "find_untagged_elements";
        public string Description =>
            "List elements of a category visible in a view (view_id, default the active view) that have no tag there. "
            + "category is a name (Doors) or built-in name (OST_Doors). limit default 200, max 500.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""category"":{""type"":""string""},
            ""view_id"":{""type"":""integer""},
            ""limit"":{""type"":""integer""}},
            ""required"":[""category""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!AnnotationHelpers.TryLimit(p, 200, 500, out var limit, out var failure)) return failure;
            if (!AnnotationHelpers.TryAnnotationView(doc, p.Value<long?>("view_id"), out View view, out failure)) return failure;
            if (!AnnotationHelpers.TryCategory(doc, p.Value<string>("category"), out var category, out failure)) return failure;

            var visible = new FilteredElementCollector(doc, view.Id).OfCategoryId(category.Id).WhereElementIsNotElementType()
                .Where(e => !(e is Room room) || room.Area > 0).ToList();
            var tagged = AnnotationHelpers.TaggedInView(doc, view);
            var untagged = visible.Where(e => !tagged.Contains(RevitCompat.GetId(e.Id))).ToList();

            return CommandResult.Ok(new JObject
            {
                ["category"] = category.Name,
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["view_name"] = view.Name,
                ["visible_count"] = visible.Count,
                ["tagged_count"] = visible.Count - untagged.Count,
                ["untagged_count"] = untagged.Count,
                ["returned"] = System.Math.Min(untagged.Count, limit),
                ["truncated"] = untagged.Count > limit,
                ["untagged"] = new JArray(untagged.Take(limit).Select(e => new JObject
                {
                    ["element_id"] = RevitCompat.GetId(e.Id),
                    ["name"] = e.Name
                }))
            });
        }
    }
}
