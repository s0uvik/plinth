// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Views/ActivateViewHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: ReadOnly (switching the active view is UI state: no transaction, nothing in the model
// changes, Undo has nothing to record); a name shared by several views is an error listing them (rvt-mcp
// took the first); a miss lists the valid views; passing neither view_id nor view_name is an error (there is
// no fallback); refuses cleanly while the document has an open transaction, where Revit cannot switch views;
// the result reports the view Revit actually made active.

using System;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.View
{
    // Plinth.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class ActivateViewHandler : IRevitCommand
    {
        public string Name => "activate_view";
        public string Description =>
            "Make a view or sheet the active view in Revit's window, by view_id or view_name. UI only: the model does not change. "
            + "Run it on its own, not inside a write transaction.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""view_id"":{""type"":""integer""},
            ""view_name"":{""type"":""string""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var id = p.Value<long?>("view_id");
            var name = p.Value<string>("view_name");
            if (!id.HasValue && string.IsNullOrWhiteSpace(name))
                return CommandResult.Fail("INVALID_PARAMS: pass view_id or view_name.");
            if (!ViewHelpers.TryResolveView(uidoc, id, name, false, out View view, out var failure)) return failure;
            if (doc.IsModifiable)
                return CommandResult.Fail("TRANSACTION_OPEN: Revit cannot change the active view while a transaction is open; run activate_view on its own.");

            try
            {
                uidoc.ActiveView = view;
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("ACTIVATE_FAILED: could not activate '" + view.Name + "' (" + view.ViewType + "): " + ex.Message);
            }

            var active = uidoc.ActiveView;
            return CommandResult.Ok(new
            {
                activated_view_id = RevitCompat.GetId(view.Id),
                view_name = view.Name,
                view_type = view.ViewType.ToString(),
                active_view_id = active == null ? (long?)null : RevitCompat.GetId(active.Id)
            });
        }
    }
}
