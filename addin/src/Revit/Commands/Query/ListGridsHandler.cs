using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Query
{
    /// <summary>New in B2M: grids with IDs and end points in mm, for read-back.</summary>
    public sealed class ListGridsHandler : IRevitCommand
    {
        public string Name => "list_grids";
        public string Description => "List grids by name with id and start/end points in mm (arc grids report their end points).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
                .OrderBy(g => g.Name)
                .Select(g =>
                {
                    var a = g.Curve.GetEndPoint(0);
                    var b = g.Curve.GetEndPoint(1);
                    return new
                    {
                        elementId = RevitCompat.GetId(g.Id),
                        name = g.Name,
                        isCurved = g.IsCurved,
                        startMm = new[] { Mm.FromFeet(a.X), Mm.FromFeet(a.Y) },
                        endMm = new[] { Mm.FromFeet(b.X), Mm.FromFeet(b.Y) }
                    };
                })
                .ToList();
            return CommandResult.Ok(new { count = grids.Count, grids });
        }
    }
}
