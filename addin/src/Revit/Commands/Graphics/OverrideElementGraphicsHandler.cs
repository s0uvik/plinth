// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Graphics/OverrideElementGraphicsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: all or nothing (rvt-mcp committed the elements that worked and listed the rest): missing
// elements and element types fail before the transaction, and any element Revit refuses rolls back the whole
// call; at most 5000 ids; failures are errors with codes; the solid fill pattern is found by IsSolidFill.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Graphics
{
    public sealed class OverrideElementGraphicsHandler : IRevitCommand
    {
        public string Name => "override_element_graphics";
        public string Description => "Override how elements look in one view: hex colours '#RRGGBB', transparency 0-100, halftone, "
            + "projection line weight 1-16. Unspecified settings keep their current values. view_id defaults to the active view. At most 5000 ids.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""view_id"":{""type"":""integer""}," + GraphicsHelpers.OverrideSchemaFields + @"},""required"":[""element_ids""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            if (!GraphicsHelpers.TryIds(p, "element_ids", true, out var ids, out var failure)) return failure;
            if (!GraphicsHelpers.TryView(doc, p.Value<long?>("view_id"), out var view, out failure)) return failure;
            if (!GraphicsHelpers.RequireOverrides(view, out failure)) return failure;
            if (!GraphicsHelpers.TryOverrides(doc, p, out var actions, out var set, out failure)) return failure;

            var targets = new List<ElementId>();
            var missing = new List<long>();
            var types = new List<long>();
            foreach (var id in ids)
            {
                var element = doc.GetElement(RevitCompat.ToElementId(id));
                if (element == null) missing.Add(id);
                else if (element is ElementType) types.Add(id);
                else targets.Add(element.Id);
            }
            if (missing.Count > 0)
                return CommandResult.Fail("ELEMENT_NOT_FOUND: " + missing.Count + " id(s) are not elements in this document; nothing changed.",
                    new JObject { ["missing_ids"] = new JArray(missing.Take(GraphicsHelpers.MaxReported)) });
            if (types.Count > 0)
                return CommandResult.Fail("ELEMENT_IS_TYPE: " + types.Count + " id(s) are element types, which have no per-view overrides; nothing changed.",
                    new JObject { ["type_ids"] = new JArray(types.Take(GraphicsHelpers.MaxReported)) });

            using (var tx = new Transaction(doc, "B2M: Override Element Graphics"))
            {
                tx.Start();
                var current = ElementId.InvalidElementId;
                try
                {
                    foreach (var id in targets)
                    {
                        current = id;
                        var settings = new OverrideGraphicSettings(view.GetElementOverrides(id));
                        foreach (var action in actions) action(settings);
                        view.SetElementOverrides(id, settings);
                    }
                    if (tx.Commit() != TransactionStatus.Committed) return GraphicsHelpers.NotCommitted();
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("OVERRIDE_FAILED: element " + RevitCompat.GetId(current) + " refused the override; nothing changed: " + ex.Message,
                        new JObject { ["element_id"] = RevitCompat.GetId(current) });
                }
            }

            var result = GraphicsHelpers.ViewRef(view);
            result["element_count"] = targets.Count;
            result["overrides_set"] = new JArray(set);
            return CommandResult.Ok(result);
        }
    }
}
