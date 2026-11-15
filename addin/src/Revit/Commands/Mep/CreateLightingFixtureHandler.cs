// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Mep/CreateLightingFixtureHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: placement shared with create_air_terminal in MepFixturePlacement (see there for the changes).

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Mep
{
    public sealed class CreateLightingFixtureHandler : IRevitCommand
    {
        public string Name => "create_lighting_fixture";
        public string Description =>
            "Place a lighting fixture at x, y in mm on a named level; z is the absolute elevation in mm (default the level). "
            + "Type by type_id or type_name (+ family_name). Hosted and face-based families need host_id (a ceiling).";
        public string ParametersSchema => MepFixturePlacement.Schema;
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p) =>
            MepFixturePlacement.Place(app, p, BuiltInCategory.OST_LightingFixtures, "lighting fixture");
    }
}
