// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Elements/GetElementRelationshipsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: the reflection lookup of a "Host" property is replaced by the typed hosts Revit has (family
// instance host, inserts hosted by walls/floors/roofs via FindInserts); adds geometry joins; dependents are capped
// (maxDependents, default 500) and exclude the element itself; at most 200 IDs per call.

using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Query
{
    public sealed class GetElementRelationshipsHandler : IRevitCommand
    {
        private const int MaxIds = 200;

        public string Name => "get_element_relationships";
        public string Description => "Relationships of elements by ID: type, group, assembly, owner view, host and hosted inserts, nested family "
            + "components, geometry joins, design option and (includeDependents, default true) dependent elements.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""includeDependents"":{""type"":""boolean""},
            ""maxDependents"":{""type"":""integer""}},""required"":[""elementIds""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            var ids = p["elementIds"].ToObject<long[]>().Distinct().ToArray();
            if (ids.Length == 0 || ids.Length > MaxIds) return CommandResult.Fail("INVALID_PARAMS: give 1 to " + MaxIds + " elementIds.");
            var includeDependents = p.Value<bool?>("includeDependents") ?? true;
            var maxDependents = p.Value<int?>("maxDependents") ?? 500;
            if (maxDependents < 1 || maxDependents > 10000) return CommandResult.Fail("INVALID_PARAMS: maxDependents must be between 1 and 10000.");

            var elements = new JArray();
            foreach (var id in ids)
            {
                var element = doc.GetElement(RevitCompat.ToElementId(id));
                elements.Add(element == null
                    ? new JObject { ["id"] = id, ["found"] = false, ["error"] = "not found" }
                    : Relationships(doc, element, includeDependents, maxDependents));
            }
            return CommandResult.Ok(new JObject { ["count"] = elements.Count, ["includeDependents"] = includeDependents, ["elements"] = elements });
        }

        private static JObject Relationships(Document doc, Element e, bool includeDependents, int maxDependents)
        {
            var r = new JObject
            {
                ["id"] = RevitCompat.GetId(e.Id),
                ["found"] = true,
                ["name"] = QueryHelpers.NameOf(e),
                ["category"] = e.Category?.Name
            };
            AddRef(doc, r, "type", e.GetTypeId());
            AddRef(doc, r, "group", e.GroupId);
            if (doc.GetElement(e.GroupId) is Group group && group.GroupType != null) AddRef(doc, r, "groupType", group.GroupType.Id);
            AddRef(doc, r, "assemblyInstance", e.AssemblyInstanceId);
            AddRef(doc, r, "ownerView", e.OwnerViewId);
            if (e.DesignOption != null) AddRef(doc, r, "designOption", e.DesignOption.Id);

            if (e is FamilyInstance fi)
            {
                if (fi.Host != null) AddRef(doc, r, "host", fi.Host.Id);
                if (fi.SuperComponent != null) AddRef(doc, r, "superComponent", fi.SuperComponent.Id);
                r["subComponentIds"] = Ids(fi.GetSubComponentIds());
            }
            if (e is HostObject host) r["hostedInsertIds"] = Ids(host.FindInserts(true, true, true, true));
            try
            {
                r["joinedElementIds"] = Ids(JoinGeometryUtils.GetJoinedElements(doc, e));
            }
            catch
            {
                // Elements that cannot join (annotations, views) have no join list.
            }
            if (includeDependents)
            {
                var dependents = e.GetDependentElements(null).Where(id => id != e.Id).ToList();
                r["dependentCount"] = dependents.Count;
                r["dependentElementIds"] = Ids(dependents.Take(maxDependents));
                r["dependentsTruncated"] = dependents.Count > maxDependents;
            }
            return r;
        }

        private static void AddRef(Document doc, JObject r, string key, ElementId id)
        {
            if (!(QueryHelpers.IdOrNull(id) is long value)) return;
            r[key + "Id"] = value;
            r[key + "Name"] = QueryHelpers.NameOf(doc.GetElement(id));
        }

        private static JArray Ids(IEnumerable<ElementId> ids) =>
            new JArray((ids ?? Enumerable.Empty<ElementId>()).Select(QueryHelpers.IdOrNull).Where(v => v.HasValue).Select(v => v.Value).OrderBy(v => v));
    }
}
