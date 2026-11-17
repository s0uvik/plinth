// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Graphics/ClearElementOverridesHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: all or nothing (rvt-mcp committed what worked and listed the rest): missing ids fail before
// the transaction and any element Revit refuses rolls back the call; explicit lists hold at most 5000 ids and
// the view scan clears at most max_elements (default 20000), reporting truncation; the response lists up to
// 100 cleared ids; failures are errors with codes.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Graphics
{
    public sealed class ClearElementOverridesHandler : IRevitCommand
    {
        public string Name => "clear_element_overrides";
        public string Description => "Reset per-element graphic overrides in a view to the defaults. Without element_ids, every element in the view "
            + "that has overrides is reset (up to max_elements, default 20000). view_id defaults to the active view.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""view_id"":{""type"":""integer""},
            ""max_elements"":{""type"":""integer""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            if (!GraphicsHelpers.TryIds(p, "element_ids", false, out var ids, out var failure)) return failure;
            if (!GraphicsHelpers.TryView(doc, p.Value<long?>("view_id"), out var view, out failure)) return failure;
            if (!GraphicsHelpers.RequireOverrides(view, out failure)) return failure;
            var cap = p.Value<int?>("max_elements") ?? 20000;
            if (cap < 1 || cap > 100000) return CommandResult.Fail("INVALID_PARAMS: max_elements must be 1 to 100000.");
            if (p["max_elements"] != null && ids.Count > 0)
                return CommandResult.Fail("INVALID_PARAMS: max_elements applies only to the view scan, without element_ids.");

            var targets = new List<ElementId>();
            var truncated = false;
            var scanned = 0;
            if (ids.Count > 0)
            {
                var missing = ids.Where(id => doc.GetElement(RevitCompat.ToElementId(id)) == null).ToList();
                if (missing.Count > 0)
                    return CommandResult.Fail("ELEMENT_NOT_FOUND: " + missing.Count + " id(s) are not elements in this document; nothing changed.",
                        new JObject { ["missing_ids"] = new JArray(missing.Take(GraphicsHelpers.MaxReported)) });
                targets.AddRange(ids.Select(RevitCompat.ToElementId));
            }
            else
            {
                foreach (var element in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
                {
                    scanned++;
                    bool overridden;
                    try
                    {
                        overridden = GraphicsHelpers.OverriddenSettings(view.GetElementOverrides(element.Id)).Count > 0;
                    }
                    catch
                    {
                        continue; // Elements without per-view overrides.
                    }
                    if (!overridden) continue;
                    if (targets.Count >= cap)
                    {
                        truncated = true;
                        break;
                    }
                    targets.Add(element.Id);
                }
            }

            if (targets.Count > 0)
            {
                using (var tx = new Transaction(doc, "Plinth: Clear Element Overrides"))
                {
                    tx.Start();
                    var current = ElementId.InvalidElementId;
                    try
                    {
                        var empty = new OverrideGraphicSettings();
                        foreach (var id in targets)
                        {
                            current = id;
                            view.SetElementOverrides(id, empty);
                        }
                        if (tx.Commit() != TransactionStatus.Committed) return GraphicsHelpers.NotCommitted();
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                        return CommandResult.Fail("CLEAR_FAILED: element " + RevitCompat.GetId(current) + " refused the reset; nothing changed: " + ex.Message,
                            new JObject { ["element_id"] = RevitCompat.GetId(current) });
                    }
                }
            }

            var result = GraphicsHelpers.ViewRef(view);
            result["mode"] = ids.Count > 0 ? "explicit" : "view_scan";
            if (ids.Count == 0) result["elements_scanned"] = scanned;
            result["cleared_count"] = targets.Count;
            result["truncated"] = truncated;
            result["cleared_ids"] = new JArray(targets.Take(GraphicsHelpers.MaxReported).Select(RevitCompat.GetId));
            return CommandResult.Ok(result);
        }
    }
}
