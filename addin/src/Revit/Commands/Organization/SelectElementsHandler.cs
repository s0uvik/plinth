// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Elements/SelectElementsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: ReadOnly is true by decision: the command only changes the user's selection in the Revit
// UI (Selection.SetElementIds), opens no transaction, and changes no document or file, so it needs no
// confirmed document. Missing explicit IDs fail with the missing list as data and nothing selected;
// duplicates are collapsed; at most 5000 IDs; a saved-selection miss lists the valid selections; a zoom
// failure is reported (zoomError) instead of swallowed; an empty saved selection clears nothing and fails.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Organization
{
    public sealed class SelectElementsHandler : IRevitCommand
    {
        public string Name => "select_elements";
        public string Description =>
            "Select elements in the Revit window, replacing the user's current selection. Give exactly one of elementIds, savedSelectionId or "
            + "savedSelectionName. zoomToSelection shows them in a view (Revit may switch the active view). Changes only the UI selection: no "
            + "transaction, the model is not modified (read-only). At most " + OrganizationHelpers.MaxElementIds + " elements.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""savedSelectionId"":{""type"":""integer""},
            ""savedSelectionName"":{""type"":""string""},
            ""zoomToSelection"":{""type"":""boolean""}}}";
        // UI selection only; see the header.
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var given = p["elementIds"]?.ToObject<long[]>();
            var savedId = p.Value<long?>("savedSelectionId");
            var savedName = p.Value<string>("savedSelectionName");
            var zoom = p.Value<bool?>("zoomToSelection") ?? false;
            var sources = (given != null ? 1 : 0) + (savedId.HasValue ? 1 : 0) + (!string.IsNullOrWhiteSpace(savedName) ? 1 : 0);
            if (sources != 1)
                return CommandResult.Fail("INVALID_PARAMS: give exactly one of elementIds, savedSelectionId or savedSelectionName.");

            ElementId[] ids;
            string source;
            long? selectionId = null;
            if (given != null)
            {
                if (given.Length == 0) return CommandResult.Fail("INVALID_PARAMS: elementIds must not be empty.");
                ids = given.Distinct().Select(RevitCompat.ToElementId).ToArray();
                source = "explicitIds";
            }
            else
            {
                if (!OrganizationHelpers.TrySelection(doc, savedId, savedName, "savedSelectionId", "savedSelectionName",
                        out var selection, out var failure)) return failure;
                ids = selection.GetElementIds().ToArray();
                source = "savedSelection";
                selectionId = RevitCompat.GetId(selection.Id);
            }
            if (ids.Length > OrganizationHelpers.MaxElementIds)
                return CommandResult.Fail("INVALID_PARAMS: at most " + OrganizationHelpers.MaxElementIds + " elements per call.");

            OrganizationHelpers.Resolve(doc, ids, out var found, out var missing);
            if (source == "explicitIds" && missing.Count > 0)
                return CommandResult.Fail("ELEMENT_NOT_FOUND: " + missing.Count + " element id(s) do not exist in this document. The selection was not changed.",
                    new JObject { ["missing_ids"] = new JArray(missing.Take(OrganizationHelpers.MaxOptions)) });
            if (found.Count == 0)
                return CommandResult.Fail("EMPTY_SELECTION: none of the saved selection's elements still exist. The selection was not changed.",
                    new JObject { ["stale_ids"] = new JArray(missing.Take(OrganizationHelpers.MaxOptions)) });

            try
            {
                uidoc.Selection.SetElementIds(found);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("SELECT_FAILED: Revit refused the selection: " + ex.Message);
            }

            string zoomError = null;
            var zoomed = false;
            if (zoom)
            {
                try
                {
                    uidoc.ShowElements(found);
                    zoomed = true;
                }
                catch (Exception ex)
                {
                    zoomError = ex.Message;
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["selected"] = true,
                ["count"] = found.Count,
                ["source"] = source,
                ["savedSelectionId"] = selectionId,
                ["elementIds"] = new JArray(found.Take(1000).Select(RevitCompat.GetId)),
                ["elementIdsTruncated"] = found.Count > 1000,
                ["staleIds"] = new JArray(missing.Take(OrganizationHelpers.MaxOptions)),
                ["zoomed"] = zoomed,
                ["zoomError"] = zoomError
            });
        }
    }
}
