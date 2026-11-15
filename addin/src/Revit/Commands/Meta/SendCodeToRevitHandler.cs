// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Runtime/SendCodeToRevitHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: refused unless developer mode is on (read per call from %LOCALAPPDATA%\B2M\addin-settings.json);
// every call, allowed or refused, is appended to logs\code-execution.jsonl with the full code and outcome; the
// code runs inside one "B2M: send_code" transaction through the shared ToolBaker.CodeRunner. The script protocol,
// enhanced options, modules, journaling, telemetry, history and receipts are not ported. Still refused inside
// batch_execute by BatchExecutor.

using System;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using B2M.Revit.Commands.ToolBaker;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Meta
{
    public sealed class SendCodeToRevitHandler : IRevitCommand
    {
        private const int MaxCodeChars = 200_000;

        public string Name => "send_code_to_revit";
        public string Description => "Developer mode only: compile and run C# inside Revit as one undoable transaction. "
            + CodeRunner.CodeContract + " Every call is logged with its full code.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""code"":{""type"":""string"",""description"":""C# method body to compile and run""}},""required"":[""code""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject parameters)
        {
            var code = parameters.Value<string>("code");
            if (!DeveloperMode.IsOn())
            {
                DeveloperMode.Log(app.Application.VersionNumber, Guid.NewGuid().ToString("N"), Name, null, code, "refused_developer_mode_off");
                return CommandResult.Fail(DeveloperMode.OffError(Name));
            }
            if (string.IsNullOrWhiteSpace(code) || code.Length > MaxCodeChars)
            {
                DeveloperMode.Log(app.Application.VersionNumber, Guid.NewGuid().ToString("N"), Name, null, code, "rejected_invalid_params");
                return CommandResult.Fail("INVALID_PARAMS: code must be 1 to " + MaxCodeChars + " characters. Nothing ran.");
            }
            return CodeRunner.AuditedRun(app, Name, null, code, new JObject(), "B2M: send_code");
        }
    }
}
