using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Query
{
    /// <summary>New in B2M: levels with IDs and elevations, lowest first, for read-back and re-confirming after a restart.</summary>
    public sealed class ListLevelsHandler : IRevitCommand
    {
        public string Name => "list_levels";
        public string Description => "List levels, lowest first, with id, name and elevation in mm.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.ProjectElevation)
                .Select(l => new
                {
                    elementId = RevitCompat.GetId(l.Id),
                    name = l.Name,
                    elevationMm = Mm.FromFeet(l.Elevation),
                    projectElevationMm = Mm.FromFeet(l.ProjectElevation)
                })
                .ToList();
            return CommandResult.Ok(new { count = levels.Count, levels });
        }
    }
}
