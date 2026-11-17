// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Infrastructure/Execution/IRevitCommand.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: each command declares ReadOnly itself (rvt-mcp kept a separate read-only list);
// params arrive already parsed and schema-checked; results carry JSON tokens and error data.

using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Bridge
{
    public interface IRevitCommand
    {
        /// <summary>Wire name, e.g. "create_level".</summary>
        string Name { get; }

        string Description { get; }

        /// <summary>JSON Schema subset checked by SchemaValidator before Execute runs.</summary>
        string ParametersSchema { get; }

        /// <summary>True when the command never changes a document or file. Writes need a confirmed document.</summary>
        bool ReadOnly { get; }

        /// <summary>Runs on Revit's main thread with schema-valid params.</summary>
        CommandResult Execute(UIApplication app, JObject parameters);
    }

    /// <summary>
    /// A write command that does not act on the active document (open_model opens another file), so the
    /// confirmed-document check does not apply and it works with no document open. It still counts as a write.
    /// </summary>
    public interface IOpensDocument
    {
    }

    public sealed class CommandResult
    {
        public bool Success { get; private set; }
        public JToken Data { get; private set; }

        /// <summary>"CODE: message".</summary>
        public string Error { get; private set; }

        public static CommandResult Ok(object data) =>
            new CommandResult { Success = true, Data = data == null ? JValue.CreateNull() : data as JToken ?? JToken.FromObject(data) };

        public static CommandResult Fail(string error, object data = null) =>
            new CommandResult { Success = false, Error = error, Data = data == null ? null : data as JToken ?? JToken.FromObject(data) };
    }
}
