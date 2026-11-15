// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Organization/ListAssembliesHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: paging is local; the DTO classes become JSON objects.

using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Query
{
    public sealed class ListAssembliesHandler : IRevitCommand
    {
        public string Name => "list_assemblies";
        public string Description => "List assembly instances, a page at a time (max_results default 100, at most 500). "
            + "includeMembers adds up to max_members_per_assembly (default 50) member IDs each.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""includeMembers"":{""type"":""boolean""},
            ""max_members_per_assembly"":{""type"":""integer""}," + QueryHelpers.PagingSchema + "}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!QueryHelpers.TryPaging(p, 100, 500, out var start, out var size, out var failure)) return failure;
            var includeMembers = p.Value<bool?>("includeMembers") ?? false;
            var maxMembers = p.Value<int?>("max_members_per_assembly") ?? 50;
            if (maxMembers < 1 || maxMembers > 500) return CommandResult.Fail("INVALID_PARAMS: max_members_per_assembly must be between 1 and 500.");

            var assemblies = new FilteredElementCollector(doc).OfClass(typeof(AssemblyInstance)).Cast<AssemblyInstance>()
                .OrderBy(a => a.Name).ThenBy(a => RevitCompat.GetId(a.Id)).ToList();
            var result = QueryHelpers.Page(assemblies, start, size, "assemblies", a =>
            {
                var members = a.GetMemberIds();
                var row = new JObject
                {
                    ["assemblyId"] = RevitCompat.GetId(a.Id),
                    ["name"] = a.Name,
                    ["typeId"] = QueryHelpers.Id(a.GetTypeId()),
                    ["typeName"] = string.IsNullOrEmpty(a.AssemblyTypeName) ? QueryHelpers.NameOf(doc.GetElement(a.GetTypeId())) : a.AssemblyTypeName,
                    ["category"] = a.Category?.Name,
                    ["namingCategoryId"] = QueryHelpers.Id(a.NamingCategoryId),
                    ["namingCategory"] = QueryHelpers.IdOrNull(a.NamingCategoryId).HasValue ? Category.GetCategory(doc, a.NamingCategoryId)?.Name : null,
                    ["memberCount"] = members.Count,
                    ["ownerViewId"] = QueryHelpers.Id(a.OwnerViewId),
                    ["ownerViewName"] = QueryHelpers.NameOf(doc.GetElement(a.OwnerViewId))
                };
                if (includeMembers)
                {
                    row["memberIds"] = new JArray(members.Select(RevitCompat.GetId).OrderBy(v => v).Take(maxMembers));
                    row["membersTruncated"] = members.Count > maxMembers;
                }
                return row;
            });
            result["includeMembers"] = includeMembers;
            return CommandResult.Ok(result);
        }
    }
}
