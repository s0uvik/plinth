// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Organization/GetAssemblyMembersHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: members keep ID order across pages (rvt-mcp re-sorted each page by category and name, so pages
// were not a stable order); WorksetId read without the 2026-removed ElementId.IntegerValue; errors carry codes.

using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Query
{
    public sealed class GetAssemblyMembersHandler : IRevitCommand
    {
        public string Name => "get_assembly_members";
        public string Description => "An assembly instance's details and its member elements, a page at a time.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""assemblyId"":{""type"":""integer""}," + QueryHelpers.PagingSchema + @"},""required"":[""assemblyId""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!QueryHelpers.TryPaging(p, 200, 1000, out var start, out var size, out var failure)) return failure;
            if (!Lookup.TryElement(doc, p.Value<long>("assemblyId"), "assembly instance", out AssemblyInstance assembly, out failure)) return failure;

            var memberIds = assembly.GetMemberIds().OrderBy(RevitCompat.GetId).ToList();
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
                    ["groupId"] = QueryHelpers.Id(member?.GroupId),
                    ["worksetId"] = member == null ? null : QueryHelpers.WorksetIdOf(member)
                };
            });
            result["assemblyId"] = RevitCompat.GetId(assembly.Id);
            result["uniqueId"] = assembly.UniqueId;
            result["name"] = assembly.Name;
            result["category"] = assembly.Category?.Name;
            result["typeId"] = QueryHelpers.Id(assembly.GetTypeId());
            result["typeName"] = QueryHelpers.NameOf(doc.GetElement(assembly.GetTypeId()));
            return CommandResult.Ok(result);
        }
    }
}
