// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Organization/CreateLevelHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: a duplicate name fails before anything is created, with the existing level's id;
// units go through Units; the transaction result is checked as before.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Create
{
    public sealed class CreateLevelHandler : IRevitCommand
    {
        public string Name => "create_level";
        public string Description => "Create a level at an elevation in mm, optionally named.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""elevation"":{""type"":""number"",""description"":""Elevation in mm""},
            ""name"":{""type"":""string""}},""required"":[""elevation""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject parameters)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var elevationMm = parameters.Value<double>("elevation");
            var name = parameters.Value<string>("name");

            if (!string.IsNullOrEmpty(name))
            {
                var existing = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                    return CommandResult.Fail("NAME_IN_USE: a level named '" + existing.Name + "' already exists.",
                        new { existingLevelId = RevitCompat.GetId(existing.Id) });
            }

            using (var tx = new Transaction(doc, "Plinth: Create Level"))
            {
                tx.Start();
                try
                {
                    var level = Level.Create(doc, Mm.ToFeet(elevationMm));
                    if (!string.IsNullOrEmpty(name)) level.Name = name;
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                    return CommandResult.Ok(new
                    {
                        elementId = RevitCompat.GetId(level.Id),
                        name = level.Name,
                        elevationMm = Mm.FromFeet(level.Elevation)
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the level: " + ex.Message);
                }
            }
        }
    }
}
