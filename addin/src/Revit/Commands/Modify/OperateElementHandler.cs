// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Elements/OperateElementHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: missing IDs and elements the active view cannot hide are reported instead of throwing; hide,
// isolate and setcolor check that the active view allows them (not a schedule or sheet without graphics overrides);
// hide, unhide and setcolor take an optional view_id to act in that view instead of the active one (the drawing set
// hides the template's own levels in its sections without switching views; isolate stays on the active view, where
// Revit shows it); setcolor adds a solid surface fill so surfaces actually show the colour; r/g/b are checked to be 0..255 (rvt-mcp
// cast them to byte and failed on larger values); default colour stays red.

using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Modify
{
    public sealed class OperateElementHandler : IRevitCommand
    {
        public string Name => "operate_element";
        public string Description => "In the active view: select, hide, unhide, isolate (temporary) or setcolor (r, g, b 0-255, default red) elements by ID. "
            + "hide, unhide and setcolor act in view_id instead when it is given.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""operation"":{""type"":""string"",""enum"":[""select"",""hide"",""unhide"",""isolate"",""setcolor""]},
            ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""view_id"":{""type"":""integer""},
            ""r"":{""type"":""integer""},""g"":{""type"":""integer""},""b"":{""type"":""integer""}},
            ""required"":[""operation"",""elementIds""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail(ModifyHelpers.NoDocument);
            if (!ModifyHelpers.TryIds(p, "elementIds", 5000, out var raw, out var failure)) return failure;
            var operation = p.Value<string>("operation");

            var found = raw.Select(id => doc.GetElement(RevitCompat.ToElementId(id))).Where(e => e != null).ToList();
            var missing = raw.Except(found.Select(e => RevitCompat.GetId(e.Id))).ToList();
            if (found.Count == 0) return CommandResult.Fail("ELEMENT_NOT_FOUND: none of the elementIds exist.", new { missingIds = missing });
            var ids = found.Select(e => e.Id).ToList();
            var view = doc.ActiveView;
            var viewId = p.Value<long?>("view_id");
            if (viewId.HasValue)
            {
                if (operation == "select" || operation == "isolate")
                    return CommandResult.Fail("INVALID_PARAMS: " + operation + " works in the active view only; leave view_id out.");
                if (!Lookup.TryView(doc, viewId.Value, out view, out var viewFailure)) return viewFailure;
            }

            if (operation == "select")
            {
                uidoc.Selection.SetElementIds(ids);
                return CommandResult.Ok(new { operation, count = ids.Count, missingIds = missing });
            }
            if (view == null) return CommandResult.Fail("NO_VIEW: there is no active view.");

            var skipped = new JArray();
            if (operation == "hide" || operation == "unhide")
            {
                var usable = found.Where(e => e.CanBeHidden(view)).Select(e => e.Id).ToList();
                foreach (var e in found.Where(e => !e.CanBeHidden(view))) skipped.Add(RevitCompat.GetId(e.Id));
                if (usable.Count == 0) return CommandResult.Fail("CANNOT_HIDE: view '" + view.Name + "' cannot hide these elements.", new { skippedIds = skipped });
                ids = usable;
            }
            if (operation == "setcolor" && !view.AreGraphicsOverridesAllowed())
                return CommandResult.Fail("VIEW_NOT_SUPPORTED: view '" + view.Name + "' does not allow graphic overrides.");

            int r = p.Value<int?>("r") ?? 255, g = p.Value<int?>("g") ?? 0, b = p.Value<int?>("b") ?? 0;
            if (new[] { r, g, b }.Any(c => c < 0 || c > 255)) return CommandResult.Fail("INVALID_PARAMS: r, g and b must be 0 to 255.");

            var result = ModifyHelpers.InTransaction(doc, operation + " elements", () =>
            {
                switch (operation)
                {
                    case "hide": view.HideElements(ids); break;
                    case "unhide": view.UnhideElements(ids); break;
                    case "isolate": view.IsolateElementsTemporary(ids); break;
                    default:
                        var ogs = ModifyHelpers.ColorOverride(doc, new Color((byte)r, (byte)g, (byte)b));
                        foreach (var id in ids) view.SetElementOverrides(id, ogs);
                        break;
                }
                return CommandResult.Ok(null);
            });
            if (!result.Success) return result;
            return CommandResult.Ok(new JObject
            {
                ["operation"] = operation,
                ["view"] = view.Name,
                ["count"] = ids.Count,
                ["color"] = operation == "setcolor" ? "RGB(" + r + "," + g + "," + b + ")" : null,
                ["missingIds"] = new JArray(missing),
                ["skippedIds"] = skipped
            });
        }
    }
}
