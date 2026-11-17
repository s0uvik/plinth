// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Annotations/FindUndimensionedElementsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: a category is matched exactly by name or built-in name (rvt-mcp also took any OST_ name ending
// with the input) and a miss lists close matches; dimension references are read through Dimension.References
// directly (the reflection fallback is gone); a dimension whose references cannot be read is counted in
// unreadable_dimensions instead of being skipped silently; limit must be 1 to 500.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Annotation
{
    // Plinth.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class FindUndimensionedElementsHandler : IRevitCommand
    {
        public string Name => "find_undimensioned_elements";
        public string Description =>
            "List elements of a category visible in a view (view_id, default the active view) that no dimension in that view "
            + "references. category is a name (Walls) or built-in name (OST_Walls). limit default 200, max 500.";
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

            var visible = new FilteredElementCollector(doc, view.Id).OfCategoryId(category.Id).WhereElementIsNotElementType().ToList();

            var dimensioned = new HashSet<long>();
            var unreadable = 0;
            foreach (var dim in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                try
                {
                    foreach (Reference r in dim.References)
                        if (r != null && r.ElementId != ElementId.InvalidElementId) dimensioned.Add(RevitCompat.GetId(r.ElementId));
                }
                catch (Exception)
                {
                    unreadable++;
                }
            }
            var undimensioned = visible.Where(e => !dimensioned.Contains(RevitCompat.GetId(e.Id))).ToList();

            return CommandResult.Ok(new JObject
            {
                ["category"] = category.Name,
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["view_name"] = view.Name,
                ["visible_count"] = visible.Count,
                ["dimensioned_count"] = visible.Count - undimensioned.Count,
                ["undimensioned_count"] = undimensioned.Count,
                ["returned"] = Math.Min(undimensioned.Count, limit),
                ["truncated"] = undimensioned.Count > limit,
                ["unreadable_dimensions"] = unreadable,
                ["undimensioned"] = new JArray(undimensioned.Take(limit).Select(e => new JObject
                {
                    ["element_id"] = RevitCompat.GetId(e.Id),
                    ["name"] = e.Name
                })),
                ["note"] = "Only dimensions owned by this view count; a dimension to a linked element counts for the link instance."
            });
        }
    }
}
