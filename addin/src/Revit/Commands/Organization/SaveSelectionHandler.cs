// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Organization/SaveSelectionHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: missing IDs fail with the missing list as data; duplicates are collapsed; at most 5000
// IDs; a name already taken by a rule-based (parameter) filter is refused up front (FilterElement.IsNameUnique);
// snake_case include_element_ids / max_element_id_results became includeElementIds / maxElementIdResults
// to match the other fields; "Plinth: ..." transaction with a commit-status check.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Organization
{
    public sealed class SaveSelectionHandler : IRevitCommand
    {
        public string Name => "save_selection";
        public string Description =>
            "Save elements as a named selection (a Revit selection filter) in the document. elementIds, or when omitted the user's current "
            + "selection in Revit (unless useActiveSelectionIfIdsOmitted is false). An existing selection of that name is replaced only with "
            + "replaceExisting: true. At most " + OrganizationHelpers.MaxElementIds + " elements.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""name"":{""type"":""string""},
            ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""replaceExisting"":{""type"":""boolean""},
            ""useActiveSelectionIfIdsOmitted"":{""type"":""boolean""},
            ""includeElementIds"":{""type"":""boolean""},
            ""maxElementIdResults"":{""type"":""integer""}},
            ""required"":[""name""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var name = (p.Value<string>("name") ?? "").Trim();
            if (name.Length == 0) return CommandResult.Fail("INVALID_PARAMS: name must not be empty.");
            var replaceExisting = p.Value<bool?>("replaceExisting") ?? false;
            var useActive = p.Value<bool?>("useActiveSelectionIfIdsOmitted") ?? true;
            var includeIds = p.Value<bool?>("includeElementIds") ?? false;
            var maxIdResults = p.Value<int?>("maxElementIdResults") ?? 100;
            if (maxIdResults < 1 || maxIdResults > 1000)
                return CommandResult.Fail("INVALID_PARAMS: maxElementIdResults must be between 1 and 1000.");

            var given = p["elementIds"]?.ToObject<long[]>();
            ElementId[] ids;
            string source;
            if (given != null && given.Length > 0)
            {
                ids = given.Distinct().Select(RevitCompat.ToElementId).ToArray();
                source = "explicitIds";
            }
            else
            {
                if (!useActive)
                    return CommandResult.Fail("INVALID_PARAMS: elementIds is empty and useActiveSelectionIfIdsOmitted is false.");
                ids = uidoc.Selection.GetElementIds().ToArray();
                if (ids.Length == 0)
                    return CommandResult.Fail("EMPTY_SELECTION: nothing is selected in Revit and no elementIds were given.");
                source = "activeSelection";
            }
            if (ids.Length > OrganizationHelpers.MaxElementIds)
                return CommandResult.Fail("INVALID_PARAMS: at most " + OrganizationHelpers.MaxElementIds + " elements per saved selection.");

            OrganizationHelpers.Resolve(doc, ids, out var found, out var missing);
            if (missing.Count > 0)
                return CommandResult.Fail("ELEMENT_NOT_FOUND: " + missing.Count + " element id(s) do not exist in this document. Nothing was saved.",
                    new JObject { ["missing_ids"] = new JArray(missing.Take(OrganizationHelpers.MaxOptions)) });

            var existing = OrganizationHelpers.Selections(doc)
                .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null && !replaceExisting)
                return CommandResult.Fail("NAME_IN_USE: a saved selection named '" + existing.Name + "' already exists; pass replaceExisting: true to replace it.",
                    new JObject { ["existingSelectionId"] = RevitCompat.GetId(existing.Id) });
            if (existing == null && !FilterElement.IsNameUnique(doc, name))
                return CommandResult.Fail("NAME_IN_USE: a view filter named '" + name + "' already exists; saved selections share names with view filters.");

            SelectionFilterElement filter;
            using (var tx = new Transaction(doc, "Plinth: Save selection"))
            {
                tx.Start();
                try
                {
                    filter = existing ?? SelectionFilterElement.Create(doc, name);
                    filter.SetElementIds(found);
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures. Nothing was saved.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("SAVE_FAILED: could not save the selection: " + ex.Message);
                }
            }

            var result = new JObject
            {
                ["saved"] = true,
                ["created"] = existing == null,
                ["replaced"] = existing != null,
                ["selectionId"] = RevitCompat.GetId(filter.Id),
                ["name"] = filter.Name,
                ["source"] = source,
                ["count"] = found.Count
            };
            if (includeIds)
            {
                result["elementIds"] = new JArray(found.Take(maxIdResults).Select(RevitCompat.GetId));
                result["elementIdsTruncated"] = found.Count > maxIdResults;
            }
            return CommandResult.Ok(result);
        }
    }
}
