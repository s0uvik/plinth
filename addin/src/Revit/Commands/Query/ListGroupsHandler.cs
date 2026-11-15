// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Organization/ListGroupsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: groups are compared by BuiltInCategory instead of casting category IDs; the list is paged
// (rvt-mcp returned every group, and with includeMembers every member ID, in one response).

using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Query
{
    public sealed class ListGroupsHandler : IRevitCommand
    {
        public string Name => "list_groups";
        public string Description => "List model, detail and attached detail groups (groupKind all|model|detail|attached), a page at a time. "
            + "includeMembers adds each group's member IDs.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""groupKind"":{""type"":""string"",""enum"":[""all"",""model"",""detail"",""attached""]},
            ""includeMembers"":{""type"":""boolean""}," + QueryHelpers.PagingSchema + "}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!QueryHelpers.TryPaging(p, 200, 1000, out var start, out var size, out var failure)) return failure;
            var kind = p.Value<string>("groupKind") ?? "all";
            var includeMembers = p.Value<bool?>("includeMembers") ?? false;

            var groups = new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>()
                .Where(g => kind == "all" || KindOf(g) == kind)
                .OrderBy(g => g.Category?.Name).ThenBy(g => g.Name).ThenBy(g => RevitCompat.GetId(g.Id))
                .ToList();
            var result = QueryHelpers.Page(groups, start, size, "groups", g =>
            {
                var members = g.GetMemberIds();
                var row = new JObject
                {
                    ["groupId"] = RevitCompat.GetId(g.Id),
                    ["name"] = g.Name,
                    ["kind"] = KindOf(g),
                    ["groupTypeId"] = QueryHelpers.Id(g.GroupType?.Id),
                    ["groupTypeName"] = g.GroupType?.Name,
                    ["isAttached"] = KindOf(g) == "attached",
                    ["attachedParentId"] = QueryHelpers.Id(g.AttachedParentId),
                    ["memberCount"] = members.Count,
                    ["ownerViewId"] = QueryHelpers.Id(g.OwnerViewId),
                    ["ownerViewName"] = QueryHelpers.NameOf(doc.GetElement(g.OwnerViewId)),
                    ["category"] = g.Category?.Name
                };
                if (includeMembers) row["memberIds"] = new JArray(members.Select(RevitCompat.GetId).OrderBy(v => v));
                return row;
            });
            result["groupKind"] = kind;
            return CommandResult.Ok(result);
        }

        internal static string KindOf(Group g)
        {
            var bic = g.Category?.BuiltInCategory ?? BuiltInCategory.INVALID;
            if (g.IsAttached || bic == BuiltInCategory.OST_IOSAttachedDetailGroups) return "attached";
            return bic == BuiltInCategory.OST_IOSModelGroups ? "model" : "detail";
        }
    }
}
