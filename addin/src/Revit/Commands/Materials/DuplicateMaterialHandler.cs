// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Materials/DuplicateMaterialHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: the duplicate-name check is case-insensitive and returns the existing id; a missing source
// lists the valid materials.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Materials
{
    public sealed class DuplicateMaterialHandler : IRevitCommand
    {
        public string Name => "duplicate_material";
        public string Description => "Duplicate a material (source_material_id or source_material_name) under new_name, keeping its graphics and assets.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""source_material_id"":{""type"":""integer""},
            ""source_material_name"":{""type"":""string""},
            ""new_name"":{""type"":""string""}},""required"":[""new_name""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var newName = (p.Value<string>("new_name") ?? "").Trim();
            if (newName.Length == 0) return CommandResult.Fail("INVALID_PARAMS: new_name must not be empty.");
            if (!MaterialsHelpers.TryMaterial(doc, p.Value<long?>("source_material_id"), p.Value<string>("source_material_name"),
                    "source_material_id", "source_material_name", out var source, out var failure)) return failure;
            var existing = MaterialsHelpers.ByName(doc, newName);
            if (existing != null)
                return CommandResult.Fail("NAME_IN_USE: a material named '" + existing.Name + "' already exists.",
                    new JObject { ["existing_material_id"] = RevitCompat.GetId(existing.Id) });

            using (var tx = new Transaction(doc, "Plinth: Duplicate material"))
            {
                tx.Start();
                try
                {
                    var copy = source.Duplicate(newName);
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                    return CommandResult.Ok(new JObject
                    {
                        ["duplicated"] = true,
                        ["source_material_id"] = RevitCompat.GetId(source.Id),
                        ["new_material_id"] = RevitCompat.GetId(copy.Id),
                        ["new_name"] = copy.Name
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("DUPLICATE_FAILED: could not duplicate '" + source.Name + "': " + ex.Message);
                }
            }
        }
    }
}
