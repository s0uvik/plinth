// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Views/CreateCalloutViewHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: registered in the annotation toolset; with no view_family_type_id the parent view's own view
// type is used (rvt-mcp took the first Detail, Section or FloorPlan type in the project, which Revit rejects when
// its family does not match the parent); a type that is neither a Detail type nor of the parent's family is refused
// with the valid ones listed; in plan parents the rectangle is model X/Y in mm (other parents: mm along the view's
// right/up from its origin, as before); a name Revit refuses rolls the callout back; mm via Mm.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Annotation
{
    // Plinth.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class CreateCalloutViewHandler : IRevitCommand
    {
        private static readonly ViewType[] Parents =
            { ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.EngineeringPlan, ViewType.AreaPlan, ViewType.Section, ViewType.Elevation, ViewType.Detail };

        public string Name => "create_callout_view";
        public string Description =>
            "Create a callout view in a parent plan, section, elevation or detail view over the rectangle min_x/min_y to max_x/max_y "
            + "(plan parents: model coordinates in mm; others: mm along the parent's right/up from its origin). "
            + "Optional view_family_type_id (a Detail type or one of the parent's family; default the parent's own type) and name.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""parent_view_id"":{""type"":""integer""},
            ""min_x"":{""type"":""number""},
            ""min_y"":{""type"":""number""},
            ""max_x"":{""type"":""number""},
            ""max_y"":{""type"":""number""},
            ""view_family_type_id"":{""type"":""integer""},
            ""name"":{""type"":""string""}},
            ""required"":[""parent_view_id"",""min_x"",""min_y"",""max_x"",""max_y""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            double minX = p.Value<double>("min_x"), minY = p.Value<double>("min_y"), maxX = p.Value<double>("max_x"), maxY = p.Value<double>("max_y");
            if (maxX <= minX || maxY <= minY)
                return CommandResult.Fail("INVALID_PARAMS: max_x/max_y must be greater than min_x/min_y.");

            if (!Lookup.TryView(doc, p.Value<long>("parent_view_id"), out View parent, out var failure)) return failure;
            if (!Parents.Contains(parent.ViewType))
                return CommandResult.Fail("VIEW_NOT_CALLOUT_PARENT: view '" + parent.Name + "' is a " + parent.ViewType
                    + "; callouts go in plan, section, elevation or detail views.");
            var parentType = doc.GetElement(parent.GetTypeId()) as ViewFamilyType;

            var valid = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .Where(t => t.ViewFamily == ViewFamily.Detail || (parentType != null && t.ViewFamily == parentType.ViewFamily))
                .OrderBy(t => t.ViewFamily.ToString()).ThenBy(t => t.Name).ToList();
            var typeId = p.Value<long?>("view_family_type_id");
            var calloutType = typeId.HasValue
                ? valid.FirstOrDefault(t => RevitCompat.GetId(t.Id) == typeId.Value)
                : parentType;
            if (calloutType == null)
                return CommandResult.Fail("TYPE_NOT_FOUND: " + (typeId.HasValue
                        ? "view type " + typeId.Value + " is neither a Detail type nor a " + parentType?.ViewFamily + " type."
                        : "the parent view has no view type; pass view_family_type_id."),
                    new JObject { ["valid_view_types"] = new JArray(valid.Take(100).Select(t => new JObject
                    {
                        ["id"] = RevitCompat.GetId(t.Id), ["name"] = t.Name, ["family"] = t.ViewFamily.ToString()
                    })) });

            // A name already used by a view of the same type makes Revit throw; that rolls the callout back.
            var name = p.Value<string>("name")?.Trim();
            View callout;
            using (var tx = new Transaction(doc, "Plinth: Create Callout View"))
            {
                tx.Start();
                try
                {
                    var a = AnnotationHelpers.ViewPoint(parent, minX, minY);
                    var b = AnnotationHelpers.ViewPoint(parent, maxX, maxY);
                    callout = ViewSection.CreateCallout(doc, parent.Id, calloutType.Id, a, b);
                    if (callout == null) throw new InvalidOperationException("Revit returned no callout view.");
                    if (!string.IsNullOrEmpty(name)) callout.Name = name;
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the callout view: " + ex.Message + " Nothing was created.");
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["callout_view_id"] = RevitCompat.GetId(callout.Id),
                ["parent_view_id"] = RevitCompat.GetId(parent.Id),
                ["name"] = callout.Name,
                ["view_type"] = callout.ViewType.ToString(),
                ["view_family_type"] = calloutType.Name,
                ["crop_mm"] = new JObject { ["min_x"] = minX, ["min_y"] = minY, ["max_x"] = maxX, ["max_y"] = maxY }
            });
        }
    }
}
