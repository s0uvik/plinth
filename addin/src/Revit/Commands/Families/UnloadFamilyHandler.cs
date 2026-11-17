// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Families/UnloadFamilyHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: refusals are errors ("CODE: message") instead of success with unloaded=false; family_id is an
// integer; a miss lists the valid families; the "IsEditable == false means system family" guard is dropped (a Family
// element is never a system family, and some loadable families are not editable); everything Revit deletes along
// with the family is counted from Document.Delete instead of being assumed.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Families
{
    public sealed class UnloadFamilyHandler : IRevitCommand
    {
        public string Name => "unload_family";
        public string Description =>
            "Delete a loadable family and its types from the document, by family_id or family_name. Placed instances block it unless "
            + "cascade_delete_instances: true. In-place families need allow_inplace: true. dry_run reports what would be deleted.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + FamiliesHelpers.FamilySchema + @",
            ""cascade_delete_instances"":{""type"":""boolean""},
            ""allow_inplace"":{""type"":""boolean""},
            ""dry_run"":{""type"":""boolean""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!FamiliesHelpers.TryFamily(doc, p.Value<long?>("family_id"), p.Value<string>("family_name"), out var family, out var failure))
                return failure;

            var cascade = p.Value<bool?>("cascade_delete_instances") ?? false;
            var allowInPlace = p.Value<bool?>("allow_inplace") ?? false;
            var dryRun = p.Value<bool?>("dry_run") ?? false;
            var familyId = RevitCompat.GetId(family.Id);
            var familyName = family.Name;

            if (family.IsInPlace && !allowInPlace)
                return CommandResult.Fail("FAMILY_IS_INPLACE: '" + familyName + "' is an in-place family; pass allow_inplace: true to delete it and its model content.");

            var symbolIds = new HashSet<long>(family.GetFamilySymbolIds().Select(RevitCompat.GetId));
            var instanceIds = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType()
                .Where(e => symbolIds.Contains(RevitCompat.GetId(e.GetTypeId()))).Select(e => e.Id).ToList();

            if (instanceIds.Count > 0 && !cascade)
                return CommandResult.Fail("FAMILY_IN_USE: '" + familyName + "' has " + instanceIds.Count
                    + " placed instance(s); pass cascade_delete_instances: true to delete them with the family.",
                    new JObject { ["family_id"] = familyId, ["instance_count"] = instanceIds.Count });

            var result = new JObject
            {
                ["family_id"] = familyId,
                ["family_name"] = familyName,
                ["kind"] = FamiliesHelpers.Kind(family),
                ["types"] = symbolIds.Count,
                ["instances"] = instanceIds.Count,
                ["dry_run"] = dryRun
            };
            if (dryRun)
            {
                result["unloaded"] = false;
                return CommandResult.Ok(result);
            }

            var deleted = new HashSet<long>();
            using (var tx = new Transaction(doc, "Plinth: Unload family"))
            {
                tx.Start();
                try
                {
                    if (instanceIds.Count > 0)
                        foreach (var id in doc.Delete(instanceIds)) deleted.Add(RevitCompat.GetId(id));
                    foreach (var id in doc.Delete(family.Id)) deleted.Add(RevitCompat.GetId(id));
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the unload back; see failures. Nothing was deleted.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("DELETE_FAILED: could not delete family '" + familyName + "': " + ex.Message);
                }
            }

            var expected = new HashSet<long>(symbolIds) { familyId };
            foreach (var id in instanceIds) expected.Add(RevitCompat.GetId(id));
            result["unloaded"] = true;
            result["deleted_count"] = deleted.Count;
            result["also_deleted"] = new JArray(deleted.Where(id => !expected.Contains(id)).OrderBy(id => id).Take(500));
            return CommandResult.Ok(result);
        }
    }
}
