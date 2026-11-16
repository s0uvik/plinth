// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Families/RenameFamilyTypeHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: failures are errors instead of success with renamed=false; a name clash is caught before the
// transaction and names the existing type; type_id is an integer.
// Exempt from duplicate before edit (TypeEditGuard): a rename changes no element's geometry or data other than the type
// name it shows, so it needs neither duplicate_as nor confirm_edit_all; the result reports instance_count so the
// caller sees how many elements now show the new name.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Families
{
    public sealed class RenameFamilyTypeHandler : IRevitCommand
    {
        public string Name => "rename_family_type";
        public string Description => "Rename a family type or system type. The new name must be unique within the family (or among types of that kind). "
            + "Renaming changes nothing but the name, so it is allowed without duplicate_as / confirm_edit_all; instance_count reports how many "
            + "elements show the new name.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""type_id"":{""type"":""integer""},
            ""new_name"":{""type"":""string""}},""required"":[""type_id"",""new_name""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!FamiliesHelpers.TryType(doc, p.Value<long>("type_id"), "type_id", out var type, out var failure)) return failure;

            var newName = (p.Value<string>("new_name") ?? "").Trim();
            if (newName.Length == 0) return CommandResult.Fail("INVALID_PARAMS: new_name must not be empty.");
            var oldName = type.Name;
            var result = new JObject
            {
                ["type_id"] = RevitCompat.GetId(type.Id),
                ["old_name"] = oldName,
                ["family_name"] = FamiliesHelpers.FamilyNameOf(type),
                ["category"] = type.Category?.Name,
                ["instance_count"] = TypeEditGuard.TypeUserCount(doc, type)
            };
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                result["renamed"] = false;
                result["new_name"] = oldName;
                return CommandResult.Ok(result);
            }
            var clash = FamiliesHelpers.NameClash(doc, type, newName);
            if (clash != null)
                return CommandResult.Fail("NAME_IN_USE: a type named '" + clash.Name + "' already exists.",
                    new JObject { ["existing_type_id"] = RevitCompat.GetId(clash.Id) });

            using (var tx = new Transaction(doc, "B2M: Rename type"))
            {
                tx.Start();
                try
                {
                    type.Name = newName;
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("RENAME_FAILED: Revit refused the name '" + newName + "': " + ex.Message);
                }
            }
            result["renamed"] = true;
            result["new_name"] = type.Name;
            return CommandResult.Ok(result);
        }
    }
}
