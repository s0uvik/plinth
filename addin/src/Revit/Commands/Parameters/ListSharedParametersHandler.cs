// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Parameters/ListSharedParametersHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: a missing or unset file is an error ("CODE: message") instead of success with exists=false;
// the path must be an absolute .txt path; a custom path is used only for this call and the user's configured
// shared parameter file is restored afterwards (as in rvt-mcp); bindings are matched by GUID through the
// document's SharedParameterElements, since bound definitions are internal ones (rvt-mcp matched ExternalDefinition
// keys only and so reported every parameter as unbound).

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Parameters
{
    public sealed class ListSharedParametersHandler : IRevitCommand
    {
        public string Name => "list_shared_parameters";
        public string Description =>
            "List the definitions in the shared parameter file (Revit's configured file, or sharedParameterFilePath for this call only), "
            + "optionally one groupName, with whether and how each is bound in the active project.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""sharedParameterFilePath"":{""type"":""string""},
            ""groupName"":{""type"":""string""},
            ""includeBindings"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            var groupFilter = p.Value<string>("groupName");
            var withBindings = (p.Value<bool?>("includeBindings") ?? true) && !doc.IsFamilyDocument;
            var limit = p.Value<int?>("limit") ?? 1000;
            if (limit < 1 || limit > 5000) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to 5000.");

            var bound = new Dictionary<Guid, KeyValuePair<Definition, ElementBinding>>();
            if (withBindings)
                foreach (var kv in ParametersHelpers.Bindings(doc))
                {
                    var guid = ParametersHelpers.SharedGuidOf(doc, kv.Key);
                    if (guid.HasValue) bound[guid.Value] = kv;
                }

            if (!ParametersHelpers.SharedParameterFile.TryOpen(app.Application, p.Value<string>("sharedParameterFilePath"), false,
                    out var file, out _, out var failure)) return failure;
            using (file)
            {
                var groups = file.File.Groups.Cast<DefinitionGroup>().ToList();
                if (!string.IsNullOrEmpty(groupFilter) && !groups.Any(g => string.Equals(g.Name, groupFilter, StringComparison.OrdinalIgnoreCase)))
                    return CommandResult.Fail("GROUP_NOT_FOUND: the file has no group named '" + groupFilter + "'.",
                        new JObject { ["valid_groups"] = new JArray(groups.Select(g => g.Name)) });

                var rows = new List<JObject>();
                foreach (var group in groups)
                {
                    if (!string.IsNullOrEmpty(groupFilter) && !string.Equals(group.Name, groupFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (Definition d in group.Definitions)
                    {
                        if (!(d is ExternalDefinition def)) continue;
                        ForgeTypeId spec = null;
                        try { spec = def.GetDataType(); } catch { }
                        var row = new JObject
                        {
                            ["name"] = def.Name,
                            ["guid"] = def.GUID.ToString("d"),
                            ["groupName"] = group.Name,
                            ["dataTypeId"] = spec?.TypeId,
                            ["dataTypeLabel"] = ParametersHelpers.SpecLabel(spec),
                            ["visible"] = def.Visible,
                            ["userModifiable"] = def.UserModifiable,
                            ["hideWhenNoValue"] = def.HideWhenNoValue,
                            ["description"] = def.Description
                        };
                        if (withBindings)
                        {
                            var isBound = bound.TryGetValue(def.GUID, out var b);
                            row["isBound"] = isBound;
                            if (isBound)
                            {
                                row["bindingKind"] = ParametersHelpers.KindOf(b.Value);
                                row["parameterGroupId"] = b.Key.GetGroupTypeId()?.TypeId;
                                row["categories"] = ParametersHelpers.CategoriesJson(b.Value);
                            }
                        }
                        rows.Add(row);
                    }
                }
                var ordered = rows.OrderBy(r => r.Value<string>("groupName"), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.Value<string>("name"), StringComparer.OrdinalIgnoreCase).ToList();
                return CommandResult.Ok(new JObject
                {
                    ["sharedParameterFilePath"] = file.Path,
                    ["groupCount"] = groups.Count,
                    ["definitionCount"] = ordered.Count,
                    ["returned"] = Math.Min(limit, ordered.Count),
                    ["limit_hit"] = ordered.Count > limit,
                    ["includeBindings"] = withBindings,
                    ["definitions"] = new JArray(ordered.Take(limit))
                });
            }
        }
    }
}
