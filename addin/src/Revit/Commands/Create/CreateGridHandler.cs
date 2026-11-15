// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Organization/CreateGridHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: duplicate names and zero-length lines fail before anything is created; the
// response reads the grid back.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Create
{
    public sealed class CreateGridHandler : IRevitCommand
    {
        public string Name => "create_grid";
        public string Description => "Create a straight grid line between two points in mm, optionally named.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""startX"":{""type"":""number""},""startY"":{""type"":""number""},
            ""endX"":{""type"":""number""},""endY"":{""type"":""number""},
            ""name"":{""type"":""string""}},""required"":[""startX"",""startY"",""endX"",""endY""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var name = p.Value<string>("name");
            if (!string.IsNullOrEmpty(name))
            {
                var existing = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
                    .FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                    return CommandResult.Fail("NAME_IN_USE: a grid named '" + existing.Name + "' already exists.",
                        new { existingGridId = RevitCompat.GetId(existing.Id) });
            }

            var start = new XYZ(Mm.ToFeet(p.Value<double>("startX")), Mm.ToFeet(p.Value<double>("startY")), 0);
            var end = new XYZ(Mm.ToFeet(p.Value<double>("endX")), Mm.ToFeet(p.Value<double>("endY")), 0);
            if (start.DistanceTo(end) <= app.Application.ShortCurveTolerance)
                return CommandResult.Fail("LINE_TOO_SHORT: start and end are closer than Revit's shortest allowed line.");

            using (var tx = new Transaction(doc, "B2M: Create Grid"))
            {
                tx.Start();
                try
                {
                    var grid = Grid.Create(doc, Line.CreateBound(start, end));
                    if (!string.IsNullOrEmpty(name)) grid.Name = name;
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                    var a = grid.Curve.GetEndPoint(0);
                    var b = grid.Curve.GetEndPoint(1);
                    return CommandResult.Ok(new
                    {
                        elementId = RevitCompat.GetId(grid.Id),
                        name = grid.Name,
                        startMm = new[] { Mm.FromFeet(a.X), Mm.FromFeet(a.Y) },
                        endMm = new[] { Mm.FromFeet(b.X), Mm.FromFeet(b.Y) }
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the grid: " + ex.Message);
                }
            }
        }
    }
}
