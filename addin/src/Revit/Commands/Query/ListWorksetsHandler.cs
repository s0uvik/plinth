// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Organization/ListWorksetsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: element counts use ElementWorksetFilter (rvt-mcp built an integer rule on the workset
// parameter); the owner lookup that reads the central model's workset info is dropped, since it touches the
// central file and can be slow on a network: Workset.Owner already reports the owner known to this session.

using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Query
{
    public sealed class ListWorksetsHandler : IRevitCommand
    {
        public string Name => "list_worksets";
        public string Description => "List the document's worksets (user, family, view, standard) with open/editable state and owner; "
            + "includeElementCounts adds the number of non-type elements on each.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""includeElementCounts"":{""type"":""boolean""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            var includeCounts = p.Value<bool?>("includeElementCounts") ?? false;
            if (!doc.IsWorkshared)
                return CommandResult.Ok(new JObject
                {
                    ["isWorkshared"] = false,
                    ["activeWorksetId"] = null,
                    ["activeWorksetName"] = null,
                    ["count"] = 0,
                    ["worksets"] = new JArray()
                });

            var table = doc.GetWorksetTable();
            var active = table.GetActiveWorksetId();
            var worksets = new FilteredWorksetCollector(doc).ToWorksets()
                .OrderBy(w => w.Kind.ToString()).ThenBy(w => w.Name).ThenBy(w => w.Id.IntegerValue)
                .Select(w =>
                {
                    var row = new JObject
                    {
                        ["id"] = w.Id.IntegerValue,
                        ["name"] = w.Name,
                        ["kind"] = w.Kind.ToString(),
                        ["isOpen"] = w.IsOpen,
                        ["isEditable"] = w.IsEditable,
                        ["owner"] = string.IsNullOrWhiteSpace(w.Owner) ? null : w.Owner
                    };
                    if (includeCounts)
                        row["elementCount"] = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                            .WherePasses(new ElementWorksetFilter(w.Id, false)).GetElementCount();
                    return row;
                }).ToList();
            return CommandResult.Ok(new JObject
            {
                ["isWorkshared"] = true,
                ["activeWorksetId"] = active.IntegerValue,
                ["activeWorksetName"] = table.GetWorkset(active)?.Name,
                ["includeElementCounts"] = includeCounts,
                ["count"] = worksets.Count,
                ["worksets"] = new JArray(worksets)
            });
        }
    }
}
