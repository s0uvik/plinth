// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/ToolBaker/RunBakedToolHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: refused unless developer mode is on; every call, allowed or refused, is appended to
// logs\code-execution.jsonl with the tool name, its code and the outcome; the tool's C# is read from its JSON file and
// compiled on each call by the same CodeRunner as send_code_to_revit, inside one "B2M: baked tool <name>" transaction;
// params are checked against the tool's schema by SchemaValidator first. The dispatcher-registered baked commands,
// authorizer, usage recording, compat warnings and output=file are not ported. Still refused inside batch_execute.

using System;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using B2M.Revit.Commands.Meta;
using B2M.Revit.Core.Execution;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.ToolBaker
{
    public sealed class RunBakedToolHandler : IRevitCommand
    {
        public string Name => "run_baked_tool";
        public string Description => "Developer mode only: run a baked tool from " + BakedToolRegistry.FolderHint
            + " by name, as one undoable transaction. params must match the tool's parameters_schema (see list_baked_tools).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""name"":{""type"":""string""},
            ""params"":{""type"":""object""}},""required"":[""name""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject parameters)
        {
            var name = parameters.Value<string>("name");
            var args = parameters["params"] as JObject ?? new JObject();
            var year = app.Application.VersionNumber;
            var tool = BakedToolRegistry.Find(name);

            if (!DeveloperMode.IsOn())
            {
                DeveloperMode.Log(year, Guid.NewGuid().ToString("N"), Name, name, tool?.Code, "refused_developer_mode_off");
                return CommandResult.Fail(DeveloperMode.OffError(Name));
            }

            string rejection = null;
            if (tool == null)
                rejection = "BAKED_TOOL_NOT_FOUND: no baked tool named '" + name + "' in " + BakedToolRegistry.FolderHint + ". Call list_baked_tools.";
            else if (tool.Problem != null)
                rejection = "BAKED_TOOL_INVALID: " + tool.FileName + ": " + tool.Problem;
            else
            {
                var validation = SchemaValidator.Validate(tool.ParametersSchema.ToString(Formatting.None), args.ToString(Formatting.None));
                if (!validation.IsValid)
                    rejection = validation.Error + (validation.Suggestion == null ? "" : " " + validation.Suggestion);
            }
            if (rejection != null)
            {
                DeveloperMode.Log(year, Guid.NewGuid().ToString("N"), Name, name, tool?.Code, "rejected", rejection);
                return CommandResult.Fail(rejection + " Nothing ran.");
            }

            var result = CodeRunner.AuditedRun(app, Name, name, tool.Code, args, "B2M: baked tool " + name);
            if (!result.Success) return result;
            return CommandResult.Ok(new JObject
            {
                ["tool_name"] = name,
                ["revit_year"] = year,
                ["executed"] = true,
                ["result"] = result.Data?["result"]
            });
        }
    }
}
