// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Graphics/ApplyFilterToViewHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: failures are errors with codes (rvt-mcp returned success with an error field); a missing
// filter or view lists the valid ones; the transaction is named "B2M: ..." and rolled back only while open.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Graphics
{
    public sealed class ApplyFilterToViewHandler : IRevitCommand
    {
        public string Name => "apply_filter_to_view";
        public string Description => "Add an existing view filter to a view (or view template) and set whether matching elements are visible. "
            + "view_id defaults to the active view.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""filter_id"":{""type"":""integer""},
            ""view_id"":{""type"":""integer""},
            ""visible"":{""type"":""boolean""}},""required"":[""filter_id""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            if (!GraphicsHelpers.TryFilter(doc, p.Value<long>("filter_id"), out var filter, out var failure)) return failure;
            if (!GraphicsHelpers.TryView(doc, p.Value<long?>("view_id"), out var view, out failure)) return failure;
            if (!GraphicsHelpers.RequireOverrides(view, out failure)) return failure;
            var visible = p.Value<bool?>("visible") ?? true;

            using (var tx = new Transaction(doc, "B2M: Apply View Filter"))
            {
                tx.Start();
                try
                {
                    var alreadyApplied = view.IsFilterApplied(filter.Id);
                    if (!alreadyApplied) view.AddFilter(filter.Id);
                    view.SetFilterVisibility(filter.Id, visible);
                    if (tx.Commit() != TransactionStatus.Committed) return GraphicsHelpers.NotCommitted();

                    var result = GraphicsHelpers.ViewRef(view);
                    result["filter_id"] = RevitCompat.GetId(filter.Id);
                    result["filter_name"] = filter.Name;
                    result["already_applied"] = alreadyApplied;
                    result["visible"] = view.GetFilterVisibility(filter.Id);
                    return CommandResult.Ok(result);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("APPLY_FAILED: could not apply filter '" + filter.Name + "' to view '" + view.Name + "': " + ex.Message);
                }
            }
        }
    }
}
