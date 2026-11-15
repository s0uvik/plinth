// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Mep/CreateAirTerminalHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: placement shared with create_lighting_fixture in MepFixturePlacement (see there for the changes).

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Mep
{
    public sealed class CreateAirTerminalHandler : IRevitCommand
    {
        public string Name => "create_air_terminal";
        public string Description =>
            "Place an air terminal (diffuser, grille) at x, y in mm on a named level; z is the absolute elevation in mm (default the level). "
            + "Type by type_id or type_name (+ family_name). Hosted and face-based families need host_id (a ceiling or duct).";
        public string ParametersSchema => MepFixturePlacement.Schema;
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p) =>
            MepFixturePlacement.Place(app, p, BuiltInCategory.OST_DuctTerminal, "air terminal");
    }
}
