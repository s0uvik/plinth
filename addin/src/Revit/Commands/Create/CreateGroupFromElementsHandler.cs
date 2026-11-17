// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Organization/CreateGroupFromElementsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: a name already used by a group type fails before anything is created (rvt-mcp let Revit throw
// mid-transaction); elements that are already in a group, are types, or are views are refused up front with their IDs;
// missing IDs are returned as data; the response reads the group back after commit.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Create
{
    public sealed class CreateGroupFromElementsHandler : IRevitCommand
    {
        private const int MaxIds = 5000;

        public string Name => "create_group_from_elements";
        public string Description => "Group at least two existing elements into a new model or detail group, optionally naming the group type.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""name"":{""type"":""string""}},""required"":[""elementIds""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            var raw = p["elementIds"].ToObject<long[]>().Distinct().ToArray();
            if (raw.Length < 2 || raw.Length > MaxIds) return CommandResult.Fail("INVALID_PARAMS: give 2 to " + MaxIds + " distinct elementIds.");

            var elements = raw.Select(id => doc.GetElement(RevitCompat.ToElementId(id))).ToList();
            var missing = raw.Where((id, i) => elements[i] == null).ToList();
            if (missing.Count > 0) return CommandResult.Fail("ELEMENT_NOT_FOUND: some elementIds do not exist. Nothing was created.", new { missingIds = missing });
            var refused = elements.Where(e => e is ElementType || e is Autodesk.Revit.DB.View || e.GroupId != ElementId.InvalidElementId)
                .Select(e => RevitCompat.GetId(e.Id)).ToList();
            if (refused.Count > 0)
                return CommandResult.Fail("ELEMENT_REFUSED: types, views and elements already in a group cannot be grouped. Nothing was created.",
                    new { refusedIds = refused });

            var name = p.Value<string>("name")?.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                var clash = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Cast<GroupType>()
                    .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                if (clash != null)
                    return CommandResult.Fail("NAME_IN_USE: a group type named '" + clash.Name + "' already exists.",
                        new { existingGroupTypeId = RevitCompat.GetId(clash.Id) });
            }

            Group group;
            using (var tx = new Transaction(doc, "Plinth: Create group from elements"))
            {
                tx.Start();
                try
                {
                    group = doc.Create.NewGroup(elements.Select(e => e.Id).ToList());
                    if (!string.IsNullOrEmpty(name)) group.GroupType.Name = name;
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures. Nothing was created.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the group: " + ex.Message);
                }
            }
            var members = group.GetMemberIds().Select(RevitCompat.GetId).OrderBy(v => v).ToList();
            return CommandResult.Ok(new
            {
                groupId = RevitCompat.GetId(group.Id),
                groupName = group.Name,
                groupTypeId = RevitCompat.GetId(group.GroupType.Id),
                groupTypeName = group.GroupType.Name,
                category = group.Category?.Name,
                memberCount = members.Count,
                memberIds = members
            });
        }
    }
}
