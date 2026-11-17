// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Graphics/RemoveFilterFromViewHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: failures are errors with codes (rvt-mcp returned success with an error field), checked
// before the transaction opens; the response lists the views that still use the filter when it was kept.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Graphics
{
    using View = Autodesk.Revit.DB.View;

    public sealed class RemoveFilterFromViewHandler : IRevitCommand
    {
        public string Name => "remove_filter_from_view";
        public string Description => "Remove a view filter from a view (view_id defaults to the active view). With delete_definition_if_unused, "
            + "also delete the filter itself when no other view or template uses it.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""filter_id"":{""type"":""integer""},
            ""view_id"":{""type"":""integer""},
            ""delete_definition_if_unused"":{""type"":""boolean""}},""required"":[""filter_id""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            if (!GraphicsHelpers.TryFilter(doc, p.Value<long>("filter_id"), out var filter, out var failure)) return failure;
            if (!GraphicsHelpers.TryView(doc, p.Value<long?>("view_id"), out var view, out failure)) return failure;
            if (!GraphicsHelpers.RequireOverrides(view, out failure)) return failure;
            var deleteIfUnused = p.Value<bool?>("delete_definition_if_unused") ?? false;
            if (!view.IsFilterApplied(filter.Id))
                return CommandResult.Fail("FILTER_NOT_APPLIED: filter '" + filter.Name + "' is not applied to view '" + view.Name + "'.",
                    new JObject
                    {
                        ["applied_filters"] = new JArray(view.GetFilters().Select(id => new JObject
                        {
                            ["id"] = RevitCompat.GetId(id),
                            ["name"] = doc.GetElement(id)?.Name
                        }))
                    });

            var filterId = filter.Id;
            var filterName = filter.Name;
            using (var tx = new Transaction(doc, "Plinth: Remove View Filter"))
            {
                tx.Start();
                try
                {
                    view.RemoveFilter(filterId);
                    var stillUsedBy = ViewsUsing(doc, filterId);
                    var deleted = false;
                    if (deleteIfUnused && stillUsedBy.Count == 0)
                    {
                        doc.Delete(filterId);
                        deleted = true;
                    }
                    if (tx.Commit() != TransactionStatus.Committed) return GraphicsHelpers.NotCommitted();

                    var result = GraphicsHelpers.ViewRef(view);
                    result["filter_id"] = RevitCompat.GetId(filterId);
                    result["filter_name"] = filterName;
                    result["definition_deleted"] = deleted;
                    result["still_used_by_count"] = stillUsedBy.Count;
                    result["still_used_by"] = new JArray(stillUsedBy.Take(GraphicsHelpers.MaxReported).Select(v => new JObject
                    {
                        ["id"] = RevitCompat.GetId(v.Id),
                        ["name"] = v.Name,
                        ["is_template"] = v.IsTemplate
                    }));
                    return CommandResult.Ok(result);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("REMOVE_FAILED: could not remove filter '" + filterName + "' from view '" + view.Name + "': " + ex.Message);
                }
            }
        }

        private static List<View> ViewsUsing(Document doc, ElementId filterId)
        {
            var users = new List<View>();
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                try
                {
                    if (v.AreGraphicsOverridesAllowed() && v.IsFilterApplied(filterId)) users.Add(v);
                }
                catch
                {
                    // View types without filter support cannot use the filter.
                }
            }
            return users;
        }
    }
}
