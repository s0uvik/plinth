// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Elements/ShowElementInViewHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: ReadOnly (activating a view, selecting and zooming are UI state; nothing in the model
// changes); ids that are not elements are an error listing them (rvt-mcp dropped them silently); an unknown
// or template view_id is an error (rvt-mcp skipped the activation silently); view activation refuses while a
// transaction is open; the result reports the active view after the zoom.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.View
{
    // B2M.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class ShowElementInViewHandler : IRevitCommand
    {
        public string Name => "show_element_in_view";
        public string Description =>
            "Show elements in Revit's window: optionally activate view_id first (activate_view, default true), select them "
            + "(select, default true) and zoom to them (zoom, default true; Revit may open another view that shows them). "
            + "UI only: the model does not change.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""view_id"":{""type"":""integer""},
            ""activate_view"":{""type"":""boolean""},
            ""select"":{""type"":""boolean""},
            ""zoom"":{""type"":""boolean""}},
            ""required"":[""element_ids""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var raw = ((JArray)p["element_ids"]).Select(t => t.Value<long>()).Distinct().ToList();
            if (raw.Count == 0) return CommandResult.Fail("INVALID_PARAMS: element_ids cannot be empty.");
            var missing = raw.Where(id => doc.GetElement(RevitCompat.ToElementId(id)) == null).ToList();
            if (missing.Count > 0)
                return CommandResult.Fail("ELEMENT_NOT_FOUND: " + missing.Count + " of element_ids are not elements in this document.",
                    new JObject { ["missing_ids"] = new JArray(missing) });
            var ids = raw.Select(RevitCompat.ToElementId).ToList();

            var viewId = p.Value<long?>("view_id");
            var doActivate = p.Value<bool?>("activate_view") ?? true;
            var doSelect = p.Value<bool?>("select") ?? true;
            var doZoom = p.Value<bool?>("zoom") ?? true;
            if (!viewId.HasValue && p["activate_view"] != null && doActivate)
                return CommandResult.Fail("INVALID_PARAMS: activate_view needs view_id.");

            View view = null;
            if (viewId.HasValue && doActivate)
            {
                if (!Lookup.TryView(doc, viewId.Value, out view, out var failure)) return failure;
                if (doc.IsModifiable)
                    return CommandResult.Fail("TRANSACTION_OPEN: Revit cannot change the active view while a transaction is open; run this on its own.");
            }

            try
            {
                if (view != null) uidoc.ActiveView = view;
                if (doSelect) uidoc.Selection.SetElementIds(ids);
                if (doZoom) uidoc.ShowElements(ids);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("SHOW_FAILED: could not show the elements: " + ex.Message);
            }

            var active = uidoc.ActiveView;
            return CommandResult.Ok(new
            {
                element_count = ids.Count,
                activated_view_id = view == null ? (long?)null : RevitCompat.GetId(view.Id),
                active_view_id = active == null ? (long?)null : RevitCompat.GetId(active.Id),
                active_view_name = active?.Name,
                selected = doSelect,
                zoomed = doZoom
            });
        }
    }
}
