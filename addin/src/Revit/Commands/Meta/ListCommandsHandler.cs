using System.Linq;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Meta
{
    /// <summary>
    /// New in Plinth: describes every command (toolset, description, parameter schema, read-only), so the service
    /// can expose the whole catalog to Claude Desktop as revit_ tools without a hand-kept list (PRD F11.2).
    /// The toolset is the command's namespace under Plinth.Revit.Commands.
    /// </summary>
    public sealed class ListCommandsHandler : IRevitCommand
    {
        private readonly CommandDispatcher _dispatcher;

        public ListCommandsHandler(CommandDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public string Name => "list_commands";
        public string Description => "List every add-in command with its toolset, description, parameter schema and whether it only reads.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""toolset"":{""type"":""string""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject parameters)
        {
            var only = parameters.Value<string>("toolset");
            var commands = new JArray(_dispatcher.Names
                .Select(_dispatcher.Get)
                .Select(c => new { command = c, toolset = Toolset(c) })
                .Where(x => string.IsNullOrEmpty(only) || x.toolset == only)
                .Select(x => new JObject
                {
                    ["name"] = x.command.Name,
                    ["toolset"] = x.toolset,
                    ["description"] = x.command.Description,
                    ["read_only"] = x.command.ReadOnly,
                    ["schema"] = JObject.Parse(string.IsNullOrWhiteSpace(x.command.ParametersSchema) ? "{}" : x.command.ParametersSchema)
                }));
            return CommandResult.Ok(new JObject { ["count"] = commands.Count, ["commands"] = commands });
        }

        private static string Toolset(IRevitCommand command)
        {
            var ns = command.GetType().Namespace ?? "";
            var last = ns.Substring(ns.LastIndexOf('.') + 1);
            return last.ToLowerInvariant();
        }
    }
}
