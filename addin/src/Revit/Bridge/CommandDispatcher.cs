// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Infrastructure/Execution/CommandDispatcher.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: a plain registry; baked-tool registration removed; the command list lives in CommandCatalog.

using System;
using System.Collections.Generic;
using System.Linq;
using B2M.Revit.Core.Health;

namespace B2M.Revit.Bridge
{
    /// <summary>Name to command lookup. Filled once at startup, read-only afterwards, so any thread may read it.</summary>
    public sealed class CommandDispatcher
    {
        private readonly Dictionary<string, IRevitCommand> _commands = new Dictionary<string, IRevitCommand>(StringComparer.Ordinal);

        public void Register(IRevitCommand command)
        {
            if (_commands.ContainsKey(command.Name)) throw new InvalidOperationException("Duplicate command: " + command.Name);
            _commands.Add(command.Name, command);
        }

        public IRevitCommand Get(string name) =>
            name != null && _commands.TryGetValue(name, out var command) ? command : null;

        public IEnumerable<string> Names => _commands.Keys.OrderBy(n => n, StringComparer.Ordinal);

        public IEnumerable<CommandInfo> Describe() =>
            Names.Select(n => new CommandInfo(n, _commands[n].ReadOnly));
    }
}
