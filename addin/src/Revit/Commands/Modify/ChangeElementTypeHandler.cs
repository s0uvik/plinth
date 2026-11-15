// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Elements/ChangeElementTypeHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: the type can also be named (typeName with familyName, matched among types valid for the first
// element) and a miss lists the valid types; elements that cannot take the type are listed with failedCount so
// batch_execute rolls back; if no element changes, nothing is committed; at most 1000 IDs.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Modify
{
    public sealed class ChangeElementTypeHandler : IRevitCommand
    {
        public string Name => "change_element_type";
        public string Description => "Change elements to another type, given by typeId or by typeName (plus familyName when names repeat). "
            + "Elements the type does not fit are listed in failed.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""typeId"":{""type"":""integer""},
            ""typeName"":{""type"":""string""},
            ""familyName"":{""type"":""string""}},""required"":[""elementIds""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail(ModifyHelpers.NoDocument);
            if (!ModifyHelpers.TryIds(p, "elementIds", 1000, out var ids, out var failure)) return failure;
            var typeId = p.Value<long?>("typeId");
            var typeName = p.Value<string>("typeName");
            var familyName = p.Value<string>("familyName");
            if (typeId.HasValue == !string.IsNullOrEmpty(typeName)) return CommandResult.Fail("INVALID_PARAMS: give typeId or typeName, not both.");

            ElementType target;
            if (typeId.HasValue)
            {
                target = doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as ElementType;
                if (target == null) return CommandResult.Fail("TYPE_NOT_FOUND: no element type has id " + typeId.Value + ".");
            }
            else
            {
                var first = ids.Select(id => doc.GetElement(RevitCompat.ToElementId(id))).FirstOrDefault(e => e != null);
                if (first == null) return CommandResult.Fail("ELEMENT_NOT_FOUND: none of the elementIds exist.");
                var valid = first.GetValidTypes().Select(doc.GetElement).OfType<ElementType>().ToList();
                var matches = valid.Where(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrEmpty(familyName) || string.Equals(t.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))).ToList();
                if (matches.Count != 1)
                    return CommandResult.Fail((matches.Count == 0 ? "TYPE_NOT_FOUND: no type named '" : "TYPE_AMBIGUOUS: several families have a type named '")
                        + typeName + "' that fits element " + RevitCompat.GetId(first.Id) + "; give familyName or typeId.",
                        new JObject
                        {
                            ["valid_types"] = new JArray((matches.Count > 1 ? matches : valid).OrderBy(t => t.FamilyName).ThenBy(t => t.Name).Take(100)
                                .Select(t => new JObject { ["id"] = RevitCompat.GetId(t.Id), ["familyName"] = t.FamilyName, ["typeName"] = t.Name }))
                        });
                target = matches[0];
            }

            var changed = new JArray();
            var failed = new JArray();
            var result = ModifyHelpers.InTransaction(doc, "Change element type", () =>
            {
                foreach (var id in ids)
                {
                    var element = doc.GetElement(RevitCompat.ToElementId(id));
                    var row = new JObject { ["elementId"] = id };
                    if (element == null)
                    {
                        row["error"] = "not found";
                        failed.Add(row);
                        continue;
                    }
                    var oldTypeId = element.GetTypeId();
                    row["oldTypeId"] = oldTypeId == ElementId.InvalidElementId ? null : (long?)RevitCompat.GetId(oldTypeId);
                    row["oldTypeName"] = doc.GetElement(oldTypeId)?.Name;
                    try
                    {
                        if (!element.IsValidType(target.Id))
                        {
                            row["error"] = "the type does not fit this element";
                            failed.Add(row);
                            continue;
                        }
                        var replacement = element.ChangeTypeId(target.Id);
                        row["resultingElementId"] = replacement != null && replacement != ElementId.InvalidElementId ? RevitCompat.GetId(replacement) : id;
                        changed.Add(row);
                    }
                    catch (Exception ex)
                    {
                        row["error"] = ex.Message;
                        failed.Add(row);
                    }
                }
                return changed.Count == 0
                    ? CommandResult.Fail("NOTHING_CHANGED: no element could take the type. Nothing was changed.", new { failed })
                    : CommandResult.Ok(null);
            });
            if (!result.Success) return result;
            return CommandResult.Ok(new JObject
            {
                ["newTypeId"] = RevitCompat.GetId(target.Id),
                ["newTypeName"] = target.Name,
                ["newFamilyName"] = target.FamilyName,
                ["requested"] = ids.Length,
                ["changedCount"] = changed.Count,
                ["failedCount"] = failed.Count,
                ["changed"] = changed,
                ["failed"] = failed
            });
        }
    }
}
