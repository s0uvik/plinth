// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Structural/CreateFoundationWallHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: the wall foundation type (foundation_type_id or foundation_type_name) is required and a miss lists
// the options (rvt-mcp used the first type); a wall that already has a wall foundation is refused; errors are "CODE: message".

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Structural
{
    public sealed class CreateFoundationWallHandler : IRevitCommand
    {
        public string Name => "create_foundation_wall";
        public string Description =>
            "Create a wall (strip) foundation under an existing wall (wall_id). Type by foundation_type_id or foundation_type_name.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""wall_id"":{""type"":""integer""},
            ""foundation_type_id"":{""type"":""integer""},
            ""foundation_type_name"":{""type"":""string""}},
            ""required"":[""wall_id""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            if (!Lookup.TryElement<Wall>(doc, p.Value<long>("wall_id"), "wall", out var wall, out var failure)) return failure;
            if (!StructuralHelpers.TryType<WallFoundationType>(doc, p.Value<long?>("foundation_type_id"), p.Value<string>("foundation_type_name"),
                    "wall foundation type", "foundation_type_id", "foundation_type_name", out var type, out failure)) return failure;
            var existing = new FilteredElementCollector(doc).OfClass(typeof(WallFoundation)).Cast<WallFoundation>()
                .FirstOrDefault(f => f.WallId == wall.Id);
            if (existing != null)
                return CommandResult.Fail("ALREADY_HAS_FOUNDATION: wall " + RevitCompat.GetId(wall.Id) + " already has wall foundation "
                    + RevitCompat.GetId(existing.Id) + ".");

            using (var tx = new Transaction(doc, "B2M: Create Wall Foundation"))
            {
                tx.Start();
                try
                {
                    var foundation = WallFoundation.Create(doc, type.Id, wall.Id);
                    if (foundation == null) throw new InvalidOperationException("Revit returned no wall foundation.");

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    return CommandResult.Ok(new
                    {
                        foundation_id = RevitCompat.GetId(foundation.Id),
                        wall_id = RevitCompat.GetId(wall.Id),
                        foundation_type_id = RevitCompat.GetId(type.Id),
                        foundation_type_name = type.Name
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the wall foundation: " + ex.Message + " Nothing was created.");
                }
            }
        }
    }
}
