// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Elements/GetSelectedElementsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: paging is local (QueryHelpers instead of ResponsePaging); each element adds builtInCategory and typeId.

using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Query
{
    public sealed class GetSelectedElementsHandler : IRevitCommand
    {
        public string Name => "get_selected_elements";
        public string Description => "List the elements selected in Revit, a page at a time (max_results default 200, at most 1000). "
            + "Selected IDs that no longer exist are listed in staleIds.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + QueryHelpers.PagingSchema + "}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc?.Document == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!QueryHelpers.TryPaging(p, 200, 1000, out var start, out var size, out var failure)) return failure;

            var doc = uidoc.Document;
            var ids = uidoc.Selection.GetElementIds().OrderBy(RevitCompat.GetId).ToList();
            var stale = new List<long>();
            var result = QueryHelpers.Page(ids, start, size, "elements", id =>
            {
                var element = doc.GetElement(id);
                if (element == null)
                {
                    stale.Add(RevitCompat.GetId(id));
                    return null;
                }
                return new JObject
                {
                    ["elementId"] = RevitCompat.GetId(id),
                    ["name"] = QueryHelpers.NameOf(element),
                    ["category"] = element.Category?.Name,
                    ["builtInCategory"] = Lookup.BuiltInName(element.Category),
                    ["typeId"] = QueryHelpers.Id(element.GetTypeId()),
                    ["typeName"] = QueryHelpers.NameOf(doc.GetElement(element.GetTypeId()))
                };
            });
            var elements = (JArray)result["elements"];
            foreach (var empty in elements.Where(t => t.Type == JTokenType.Null).ToList()) empty.Remove();
            result["staleIds"] = new JArray(stale);
            return CommandResult.Ok(result);
        }
    }
}
