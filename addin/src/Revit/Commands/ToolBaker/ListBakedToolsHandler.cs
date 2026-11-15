// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/ToolBaker/ListBakedToolsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: reads the JSON-file registry under %LOCALAPPDATA%\B2M\baked-tools instead of SQLite; usage stats,
// lifecycle, compat maps and paging cursors are gone (a plain limit remains); unusable files are listed with the reason;
// reports whether developer mode is on, since run_baked_tool needs it. Works without developer mode.

using System;
using System.Linq;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using B2M.Revit.Commands.Meta;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.ToolBaker
{
    public sealed class ListBakedToolsHandler : IRevitCommand
    {
        public string Name => "list_baked_tools";
        public string Description => "List the baked tools in " + BakedToolRegistry.FolderHint
            + " (name, description, parameter schema). Running one with run_baked_tool needs developer mode.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""name_filter"":{""type"":""string""},
            ""include_code"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject parameters)
        {
            var filter = parameters.Value<string>("name_filter");
            var includeCode = parameters.Value<bool?>("include_code") ?? false;
            var limit = parameters.Value<int?>("limit") ?? 100;
            if (limit < 1 || limit > 500) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to 500.");

            var all = BakedToolRegistry.LoadAll()
                .Where(t => string.IsNullOrEmpty(filter) || t.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            var tools = new JArray(all.Take(limit).Select(t =>
            {
                var item = new JObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters_schema"] = t.ParametersSchema,
                    ["file"] = t.FileName,
                    ["source"] = t.Source,
                    ["created_utc"] = t.CreatedUtc,
                    ["usable"] = t.Problem == null
                };
                if (t.Problem != null) item["problem"] = t.Problem;
                if (includeCode) item["code"] = t.Code;
                return item;
            }));

            return CommandResult.Ok(new JObject
            {
                ["folder"] = BakedToolRegistry.FolderHint,
                ["developer_mode"] = DeveloperMode.IsOn(),
                ["count"] = all.Count,
                ["returned"] = tools.Count,
                ["truncated"] = all.Count > tools.Count,
                ["tools"] = tools
            });
        }
    }
}
