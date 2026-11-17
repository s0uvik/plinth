// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Views/SetViewScaleHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: scale is limited to 1..24000 as in create_view; view templates are refused through
// Lookup.TryView with the Plinth error codes; a view template that controls the scale is named in the error.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.View
{
    // Plinth.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class SetViewScaleHandler : IRevitCommand
    {
        public string Name => "set_view_scale";
        public string Description => "Set a view's scale (view_id, default the active view). scale is the denominator: 50 for 1:50.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""view_id"":{""type"":""integer""},
            ""scale"":{""type"":""integer""}},
            ""required"":[""scale""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var scale = p.Value<int>("scale");
            if (scale < 1 || scale > 24000)
                return CommandResult.Fail("INVALID_PARAMS: scale is the denominator of 1:N and must be 1 to 24000.");
            if (!ViewHelpers.TryResolveView(uidoc, p.Value<long?>("view_id"), null, false, out var view, out var failure)) return failure;

            var previous = view.Scale;
            using (var tx = new Transaction(doc, "Plinth: Set View Scale"))
            {
                tx.Start();
                try
                {
                    // VIEW_SCALE reports read-only on ordinary plans; View.Scale is the supported write path.
                    view.Scale = scale;
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    var template = doc.GetElement(view.ViewTemplateId) as View;
                    var hint = template != null ? " View template '" + template.Name + "' may control the scale." : "";
                    return CommandResult.Fail("SCALE_FAILED: could not set the scale of '" + view.Name + "' (" + view.ViewType + "): "
                        + ex.Message + hint + " Nothing was changed.");
                }
            }

            return CommandResult.Ok(new
            {
                view_id = RevitCompat.GetId(view.Id),
                view_name = view.Name,
                previous_scale = previous,
                new_scale = view.Scale
            });
        }
    }
}
