// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Organization/GetGroupMembersHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: errors carry codes; paging is local; the per-property try/catch wrappers are dropped.

using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Query
{
    public sealed class GetGroupMembersHandler : IRevitCommand
    {
        public string Name => "get_group_members";
        public string Description => "A group's details and its member elements, a page at a time.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""groupId"":{""type"":""integer""}," + QueryHelpers.PagingSchema + @"},""required"":[""groupId""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!QueryHelpers.TryPaging(p, 200, 1000, out var start, out var size, out var failure)) return failure;
            if (!Lookup.TryElement(doc, p.Value<long>("groupId"), "group", out Group group, out failure)) return failure;

            var memberIds = group.GetMemberIds().OrderBy(RevitCompat.GetId).ToList();
            var result = QueryHelpers.Page(memberIds, start, size, "members", id =>
            {
                var member = doc.GetElement(id);
                return new JObject
                {
                    ["elementId"] = RevitCompat.GetId(id),
                    ["name"] = QueryHelpers.NameOf(member),
                    ["category"] = member?.Category?.Name,
                    ["typeId"] = QueryHelpers.Id(member?.GetTypeId()),
                    ["typeName"] = QueryHelpers.NameOf(member == null ? null : doc.GetElement(member.GetTypeId())),
                    ["ownerViewId"] = QueryHelpers.Id(member?.OwnerViewId),
                    ["isPinned"] = member?.Pinned
                };
            });
            result["group"] = new JObject
            {
                ["groupId"] = RevitCompat.GetId(group.Id),
                ["name"] = group.Name,
                ["kind"] = ListGroupsHandler.KindOf(group),
                ["category"] = group.Category?.Name,
                ["groupTypeId"] = QueryHelpers.Id(group.GroupType?.Id),
                ["groupTypeName"] = group.GroupType?.Name,
                ["attachedParentId"] = QueryHelpers.Id(group.AttachedParentId),
                ["ownerViewId"] = QueryHelpers.Id(group.OwnerViewId),
                ["ownerViewName"] = QueryHelpers.NameOf(doc.GetElement(group.OwnerViewId)),
                ["isPinned"] = group.Pinned,
                ["memberCount"] = memberIds.Count
            };
            return CommandResult.Ok(result);
        }
    }
}
