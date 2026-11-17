// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Documents/GetProjectCoordinateSystemHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: the snapshot builder moved to LinksHelpers.CoordinateSystem (shared with the link and
// coordinate-writing commands); lengths through Mm; weather station dropped from the site block.

using System.Collections.Generic;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Links
{
    public sealed class GetProjectCoordinateSystemHandler : IRevitCommand
    {
        public string Name => "get_project_coordinate_system";
        public string Description =>
            "Read the project's internal origin, project base point, survey point, active and named project locations with shared "
            + "coordinates (east/west, north/south, elevation in mm; angle to true north in degrees) and site latitude/longitude.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (doc.IsFamilyDocument) return CommandResult.Fail("NOT_A_PROJECT: coordinate systems exist only in project documents.");

            var warnings = new List<string>();
            var snapshot = LinksHelpers.CoordinateSystem(doc, warnings);
            snapshot["warnings"] = new JArray(warnings);
            return CommandResult.Ok(snapshot);
        }
    }
}
