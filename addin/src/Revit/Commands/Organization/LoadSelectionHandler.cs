// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Organization/LoadSelectionHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: like rvt-mcp it only reads the saved selection and returns its element IDs; it does
// not change the user's selection in Revit (select_elements with savedSelectionId does that). ResponsePaging
// is replaced by inline paging (startIndex / maxResults, renamed from start_index / max_results); a miss or
// an ambiguous name lists the valid selections; element summaries carry the built-in category name.

using System;
using System.Linq;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Organization
{
    public sealed class LoadSelectionHandler : IRevitCommand
    {
        public string Name => "load_selection";
        public string Description =>
            "Read a saved selection (by selectionId or name) and return its element IDs, paged by startIndex / maxResults (default 200, max 1000). "
            + "staleIds are saved IDs whose elements no longer exist. Does not change the selection in Revit; use select_elements for that.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""name"":{""type"":""string""},
            ""selectionId"":{""type"":""integer""},
            ""includeElementSummary"":{""type"":""boolean""},
            ""startIndex"":{""type"":""integer""},
            ""maxResults"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var includeSummary = p.Value<bool?>("includeElementSummary") ?? false;
            var start = p.Value<int?>("startIndex") ?? 0;
            var max = p.Value<int?>("maxResults") ?? 200;
            if (start < 0) return CommandResult.Fail("INVALID_PARAMS: startIndex must be at least 0.");
            if (max < 1 || max > 1000) return CommandResult.Fail("INVALID_PARAMS: maxResults must be between 1 and 1000.");

            if (!OrganizationHelpers.TrySelection(doc, p.Value<long?>("selectionId"), p.Value<string>("name"), "selectionId", "name",
                    out var selection, out var failure)) return failure;

            var all = selection.GetElementIds().OrderBy(RevitCompat.GetId).ToList();
            var page = all.Skip(start).Take(max).ToList();
            OrganizationHelpers.Resolve(doc, page, out var found, out var stale);

            var result = new JObject
            {
                ["selectionId"] = RevitCompat.GetId(selection.Id),
                ["name"] = selection.Name,
                ["count"] = all.Count,
                ["startIndex"] = start,
                ["returnedCount"] = page.Count,
                ["truncated"] = start + page.Count < all.Count,
                ["nextIndex"] = start + page.Count < all.Count ? (JToken)(start + page.Count) : null,
                ["elementIds"] = new JArray(page.Select(RevitCompat.GetId)),
                ["staleIds"] = new JArray(stale)
            };
            if (includeSummary)
                result["elements"] = new JArray(found.Select(id => OrganizationHelpers.ElementSummary(doc, doc.GetElement(id))));
            return CommandResult.Ok(result);
        }
    }
}
