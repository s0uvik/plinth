// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Mep/CreateAirTerminalHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: placement shared with create_lighting_fixture in MepFixturePlacement (see there for the changes).

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Mep
{
    public sealed class CreateAirTerminalHandler : IRevitCommand
    {
        public string Name => "create_air_terminal";
        public string Description =>
            "Place an air terminal (diffuser, grille) on a named level. Type by type_id or type_name (+ family_name). "
            + "Level-hosted families may take host_id of a duct. " + MepFixturePlacement.OptionsText;
        public string ParametersSchema => MepFixturePlacement.Schema;
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p) =>
            MepFixturePlacement.Place(app, p, BuiltInCategory.OST_DuctTerminal, "air terminal");
    }
}
