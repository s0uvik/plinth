// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Runtime/ShowMessageHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: message is required and bounded; localization, the default greeting, echo options and the
// failure-control dialog flag are not ported.

using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Meta
{
    public sealed class ShowMessageHandler : IRevitCommand
    {
        private const int MaxMessage = 4000;
        private const int MaxTitle = 200;

        public string Name => "show_message";
        public string Description => "Show a message to the user in a Revit dialog. Revit waits until the user closes it.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""message"":{""type"":""string""},
            ""title"":{""type"":""string""}},""required"":[""message""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject parameters)
        {
            var message = parameters.Value<string>("message");
            var title = parameters.Value<string>("title");
            if (string.IsNullOrWhiteSpace(message)) return CommandResult.Fail("INVALID_PARAMS: message must not be empty.");
            if (message.Length > MaxMessage) return CommandResult.Fail("INVALID_PARAMS: message is over " + MaxMessage + " characters.");
            if (string.IsNullOrWhiteSpace(title)) title = "Plinth";
            if (title.Length > MaxTitle) return CommandResult.Fail("INVALID_PARAMS: title is over " + MaxTitle + " characters.");

            TaskDialog.Show(title, message);
            return CommandResult.Ok(new JObject { ["displayed"] = true, ["title"] = title, ["message_chars"] = message.Length });
        }
    }
}
