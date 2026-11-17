// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Graphics/SetFilterOverridesHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: failures are errors with codes (rvt-mcp returned success with an error field); at least one
// override is required; the solid fill pattern is found by IsSolidFill and its absence is an error; the
// response reads back which settings the filter now overrides.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Graphics
{
    public sealed class SetFilterOverridesHandler : IRevitCommand
    {
        public string Name => "set_filter_overrides";
        public string Description => "Set graphic overrides (hex colours '#RRGGBB', transparency 0-100, halftone, projection line weight 1-16) "
            + "for a filter already applied to a view. Unspecified settings keep their current values. view_id defaults to the active view.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""filter_id"":{""type"":""integer""},
            ""view_id"":{""type"":""integer""}," + GraphicsHelpers.OverrideSchemaFields + @"},""required"":[""filter_id""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            if (!GraphicsHelpers.TryFilter(doc, p.Value<long>("filter_id"), out var filter, out var failure)) return failure;
            if (!GraphicsHelpers.TryView(doc, p.Value<long?>("view_id"), out var view, out failure)) return failure;
            if (!GraphicsHelpers.RequireOverrides(view, out failure)) return failure;
            if (!view.IsFilterApplied(filter.Id))
                return CommandResult.Fail("FILTER_NOT_APPLIED: filter '" + filter.Name + "' is not applied to view '" + view.Name
                    + "'; apply it first with apply_filter_to_view.");
            if (!GraphicsHelpers.TryOverrides(doc, p, out var actions, out var set, out failure)) return failure;

            using (var tx = new Transaction(doc, "Plinth: Set Filter Overrides"))
            {
                tx.Start();
                try
                {
                    var settings = new OverrideGraphicSettings(view.GetFilterOverrides(filter.Id));
                    foreach (var action in actions) action(settings);
                    view.SetFilterOverrides(filter.Id, settings);
                    if (tx.Commit() != TransactionStatus.Committed) return GraphicsHelpers.NotCommitted();

                    var result = GraphicsHelpers.ViewRef(view);
                    result["filter_id"] = RevitCompat.GetId(filter.Id);
                    result["filter_name"] = filter.Name;
                    result["overrides_set"] = new JArray(set);
                    result["overridden_settings"] = new JArray(GraphicsHelpers.OverriddenSettings(view.GetFilterOverrides(filter.Id)).Cast<object>());
                    return CommandResult.Ok(result);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("OVERRIDE_FAILED: could not set overrides for filter '" + filter.Name + "' in view '" + view.Name + "': " + ex.Message);
                }
            }
        }
    }
}
