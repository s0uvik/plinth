// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Families/DuplicateFamilyTypeHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: all or nothing, so a parameter override that fails rolls back the whole duplicate (rvt-mcp
// committed the new type and listed the failures); a missing parameter lists the type's writable parameters;
// overrides use the shared value rules (lengths in mm through Mm, areas m2, volumes m3, angles degrees, other
// measured values need unit "internal"); source_type_id is an integer; failures are errors, not success payloads.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using B2M.Revit.Commands.Parameters;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Families
{
    public sealed class DuplicateFamilyTypeHandler : IRevitCommand
    {
        public string Name => "duplicate_family_type";
        public string Description =>
            "Duplicate a family type or system type (wall, floor...) under a new name, optionally setting type parameters: "
            + "type_parameter_overrides maps parameter name to value (lengths in mm, areas m2, volumes m3, angles degrees, "
            + "Yes/No as true/false, element parameters as an id). Any failed override rolls everything back.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""source_type_id"":{""type"":""integer""},
            ""new_type_name"":{""type"":""string""},
            ""type_parameter_overrides"":{""type"":""object""}},""required"":[""source_type_id"",""new_type_name""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!FamiliesHelpers.TryType(doc, p.Value<long>("source_type_id"), "source_type_id", out var source, out var failure)) return failure;

            var newName = (p.Value<string>("new_type_name") ?? "").Trim();
            if (newName.Length == 0) return CommandResult.Fail("INVALID_PARAMS: new_type_name must not be empty.");
            var clash = FamiliesHelpers.NameClash(doc, source, newName);
            if (clash != null)
                return CommandResult.Fail("NAME_IN_USE: a type named '" + clash.Name + "' already exists" + (source is FamilySymbol ? " in this family." : "."),
                    new JObject { ["existing_type_id"] = RevitCompat.GetId(clash.Id) });
            var overrides = p["type_parameter_overrides"] as JObject ?? new JObject();

            using (var tx = new Transaction(doc, "B2M: Duplicate type"))
            {
                tx.Start();
                try
                {
                    var created = source.Duplicate(newName);
                    if (!FamiliesHelpers.TrySetTypeParameters(doc, created, overrides, out var set, out var error, out var errorData))
                    {
                        tx.RollBack();
                        return CommandResult.Fail(error + " Nothing was created.", errorData);
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                    return CommandResult.Ok(new JObject
                    {
                        ["duplicated"] = true,
                        ["source_type_id"] = RevitCompat.GetId(source.Id),
                        ["new_type_id"] = RevitCompat.GetId(created.Id),
                        ["new_type_name"] = created.Name,
                        ["family_name"] = FamiliesHelpers.FamilyNameOf(created),
                        ["category"] = created.Category?.Name,
                        ["parameters_set"] = set
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
