// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), files
// src/shared/Handlers/Workflows/SurveyChangeImpactHandler.cs, src/shared/Survey/SurveyContract.cs and
// src/shared/Survey/SurveyEngine.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: a compact one-pass survey instead of rvt-mcp's 1,400-line engine. Kept: explicit targets (max 25),
// scopeThreshold with no default, changeKind, per-relation counts with capped ID lists, the stop flags
// (touchesType, touchesGroup, touchesLevelOrGrid, overThreshold, requiresDiscussion) and an explicit blindSpots list.
// Relations: type, host, join, connector, group, datum, annotations, dependents, proximity, presentation (owner view only).
// Dropped: depth 2, the time/scan/graph budgets (replaced by a fixed scan cap), view and sheet visibility scanning,
// room/space containment and phases, the snapshot cache and change-history bridge.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Query
{
    public sealed class SurveyChangeImpactHandler : IRevitCommand
    {
        private const int MaxTargets = 25;
        private const int MaxScanned = 200000;
        private static readonly string[] Kinds =
            { "type", "host", "join", "connector", "group", "datum", "annotations", "dependents", "proximity", "presentation" };

        public string Name => "survey_change_impact";
        public string Description =>
            "Before changing up to 25 elements, list what else the change can reach: instances of the same type, hosts and inserts, "
            + "joins, MEP connections, groups and assemblies, levels/grids, tags and dimensions, dependents and nearby elements "
            + "(within proximityPaddingMm, default 100). scopeThreshold (required) is the number of affected elements above which "
            + "the change should be discussed with the user. changeKind: instance, type or unknown (default).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""scopeThreshold"":{""type"":""integer""},
            ""changeKind"":{""type"":""string"",""enum"":[""instance"",""type"",""unknown""]},
            ""proximityPaddingMm"":{""type"":""number""},
            ""maxIdsPerRelation"":{""type"":""integer""}},""required"":[""elementIds"",""scopeThreshold""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            var ids = p["elementIds"].ToObject<long[]>().Distinct().OrderBy(v => v).ToArray();
            if (ids.Length == 0 || ids.Length > MaxTargets) return CommandResult.Fail("INVALID_PARAMS: give 1 to " + MaxTargets + " elementIds.");
            var threshold = p.Value<int>("scopeThreshold");
            if (threshold < 1 || threshold > 200000) return CommandResult.Fail("INVALID_PARAMS: scopeThreshold must be between 1 and 200000.");
            var changeKind = p.Value<string>("changeKind") ?? "unknown";
            var padding = p.Value<double?>("proximityPaddingMm") ?? 100;
            if (double.IsNaN(padding) || padding < 0 || padding > 10000) return CommandResult.Fail("INVALID_PARAMS: proximityPaddingMm must be 0 to 10000.");
            var maxIds = p.Value<int?>("maxIdsPerRelation") ?? 100;
            if (maxIds < 1 || maxIds > 200) return CommandResult.Fail("INVALID_PARAMS: maxIdsPerRelation must be between 1 and 200.");

            var survey = new Survey(doc, changeKind, Mm.ToFeet(padding));
            var targets = new JArray();
            var missing = false;
            foreach (var raw in ids)
            {
                var e = doc.GetElement(RevitCompat.ToElementId(raw));
                if (e == null)
                {
                    missing = true;
                    targets.Add(new JObject { ["id"] = raw, ["status"] = "not_found" });
                    continue;
                }
                survey.Targets[e.Id] = e;
                targets.Add(new JObject
                {
                    ["id"] = raw,
                    ["status"] = "resolved",
                    ["uniqueId"] = e.UniqueId,
                    ["name"] = QueryHelpers.NameOf(e),
                    ["category"] = e.Category?.Name,
                    ["isType"] = e is ElementType,
                    ["typeId"] = QueryHelpers.Id(e is ElementType ? e.Id : e.GetTypeId())
                });
            }
            survey.Run();

            var affected = new HashSet<ElementId>(survey.Targets.Keys);
            foreach (var kind in Kinds) affected.UnionWith(survey.Relations[kind]);
            var complete = survey.Partial.Count == 0;
            var over = affected.Count > threshold;
            return CommandResult.Ok(new JObject
            {
                ["targets"] = targets,
                ["changeKind"] = changeKind,
                ["relations"] = new JArray(Kinds.Select(kind =>
                {
                    var found = survey.Relations[kind].Select(RevitCompat.GetId).OrderBy(v => v).ToList();
                    return new JObject
                    {
                        ["kind"] = kind,
                        ["count"] = found.Count,
                        ["ids"] = new JArray(found.Take(maxIds)),
                        ["idsTruncated"] = found.Count > maxIds,
                        ["partial"] = survey.Partial.TryGetValue(kind, out var why) ? why : null
                    };
                })),
                ["scope"] = new JObject
                {
                    ["count"] = affected.Count,
                    ["countKind"] = complete ? "exact" : "lower_bound",
                    ["threshold"] = threshold,
                    ["meaning"] = "targets plus elements a change can propagate to; not a prediction of what will change"
                },
                ["flags"] = new JObject
                {
                    ["touchesType"] = survey.TouchesType,
                    ["touchesGroup"] = survey.TouchesGroup,
                    ["touchesLevelOrGrid"] = survey.TouchesDatum,
                    ["overThreshold"] = over,
                    ["unknownChangeKind"] = changeKind == "unknown",
                    ["missingTargets"] = missing,
                    ["incompleteCoverage"] = !complete,
                    ["requiresDiscussion"] = survey.TouchesType || survey.TouchesGroup || survey.TouchesDatum || over
                        || missing || changeKind == "unknown" || !complete
                },
                ["blindSpots"] = new JArray("view_and_sheet_visibility", "schedule_rows", "rooms_spaces_and_areas", "linked_models",
                    "second_order_propagation", "engineering_calculations", "design_intent", "cost_and_programme")
            });
        }

        private sealed class Survey
        {
            private readonly Document _doc;
            private readonly string _changeKind;
            private readonly double _paddingFeet;
            private int _scanned;

            public Survey(Document doc, string changeKind, double paddingFeet)
            {
                _doc = doc;
                _changeKind = changeKind;
                _paddingFeet = paddingFeet;
                foreach (var kind in Kinds) Relations[kind] = new HashSet<ElementId>();
            }

            public Dictionary<ElementId, Element> Targets { get; } = new Dictionary<ElementId, Element>();
            public Dictionary<string, HashSet<ElementId>> Relations { get; } = new Dictionary<string, HashSet<ElementId>>();
            public Dictionary<string, string> Partial { get; } = new Dictionary<string, string>();
            public bool TouchesType { get; private set; }
            public bool TouchesGroup { get; private set; }
            public bool TouchesDatum { get; private set; }

            public void Run()
            {
                foreach (var e in Targets.Values)
                {
                    Safe("type", () => TypeRelation(e));
                    Safe("host", () => HostRelation(e));
                    Safe("join", () => Add("join", JoinGeometryUtils.GetJoinedElements(_doc, e)));
                    Safe("connector", () => ConnectorRelation(e));
                    Safe("group", () => GroupRelation(e));
                    Safe("datum", () => DatumRelation(e));
                    Safe("dependents", () => Add("dependents", e.GetDependentElements(null)));
                    Safe("proximity", () => ProximityRelation(e));
                    Add("presentation", new[] { e.OwnerViewId });
                }
                Safe("annotations", AnnotationRelation);
                Partial["presentation"] = "owner view only; views, sheets and schedules showing the targets are not scanned";
            }

            private void Safe(string kind, Action action)
            {
                try
                {
                    action();
                }
                catch (ScanLimitException)
                {
                    Partial[kind] = "scan cap of " + MaxScanned + " elements reached";
                }
                catch (Exception ex)
                {
                    Partial[kind] = "not fully checked: " + ex.GetType().Name;
                }
            }

            private void Add(string kind, IEnumerable<ElementId> ids)
            {
                foreach (var id in ids ?? Enumerable.Empty<ElementId>())
                    if (id != null && id != ElementId.InvalidElementId && !Targets.ContainsKey(id)) Relations[kind].Add(id);
            }

            private IEnumerable<Element> Scan(FilteredElementCollector collector)
            {
                foreach (var element in collector)
                {
                    if (++_scanned > MaxScanned) throw new ScanLimitException();
                    yield return element;
                }
            }

            private void TypeRelation(Element e)
            {
                var type = e as ElementType ?? (_changeKind == "type" ? _doc.GetElement(e.GetTypeId()) as ElementType : null);
                if (type == null)
                {
                    Add("type", new[] { e.GetTypeId() });
                    return;
                }
                TouchesType = true;
                Add("type", new[] { type.Id });
                var collector = type is FamilySymbol
                    ? new FilteredElementCollector(_doc).WherePasses(new FamilyInstanceFilter(_doc, type.Id))
                    : type.Category != null
                        ? new FilteredElementCollector(_doc).OfCategoryId(type.Category.Id).WhereElementIsNotElementType()
                        : new FilteredElementCollector(_doc).WhereElementIsNotElementType();
                Add("type", Scan(collector).Where(x => x.GetTypeId() == type.Id).Select(x => x.Id));
            }

            private void HostRelation(Element e)
            {
                if (e is FamilyInstance fi)
                {
                    Add("host", new[] { fi.Host?.Id ?? ElementId.InvalidElementId, fi.SuperComponent?.Id ?? ElementId.InvalidElementId });
                    Add("host", fi.GetSubComponentIds());
                }
                if (e is HostObject host) Add("host", host.FindInserts(true, true, true, true));
            }

            private void ConnectorRelation(Element e)
            {
                var manager = (e as MEPCurve)?.ConnectorManager ?? (e as FamilyInstance)?.MEPModel?.ConnectorManager;
                if (manager == null) return;
                foreach (Connector connector in manager.Connectors)
                {
                    if (!connector.IsConnected) continue;
                    foreach (Connector other in connector.AllRefs)
                        if (other.Owner != null && other.Owner.Id != e.Id) Add("connector", new[] { other.Owner.Id });
                }
            }

            private void GroupRelation(Element e)
            {
                if (_doc.GetElement(e.GroupId) is Group parent)
                {
                    TouchesGroup = true;
                    Add("group", new[] { parent.Id });
                    Add("group", parent.GetMemberIds());
                    if (parent.GroupType != null) Add("group", parent.GroupType.Groups.Cast<Group>().Select(g => g.Id));
                }
                if (e is Group group)
                {
                    TouchesGroup = true;
                    Add("group", group.GetMemberIds());
                    if (group.GroupType != null) Add("group", group.GroupType.Groups.Cast<Group>().Select(g => g.Id));
                }
                if (e is GroupType groupType)
                {
                    TouchesGroup = true;
                    Add("group", groupType.Groups.Cast<Group>().Select(g => g.Id));
                }
                if (_doc.GetElement(e.AssemblyInstanceId) is AssemblyInstance assembly)
                {
                    Add("group", new[] { assembly.Id });
                    Add("group", assembly.GetMemberIds());
                }
                if (e is AssemblyInstance own) Add("group", own.GetMemberIds());
            }

            private void DatumRelation(Element e)
            {
                Add("datum", new[] { e.LevelId });
                foreach (var bip in new[] { BuiltInParameter.WALL_BASE_CONSTRAINT, BuiltInParameter.WALL_HEIGHT_TYPE,
                             BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.SCHEDULE_LEVEL_PARAM,
                             BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM })
                {
                    var parameter = e.get_Parameter(bip);
                    if (parameter != null && parameter.StorageType == StorageType.ElementId) Add("datum", new[] { parameter.AsElementId() });
                }
                if (e is Level level)
                {
                    TouchesDatum = true;
                    Add("datum", Scan(new FilteredElementCollector(_doc).WherePasses(new ElementLevelFilter(level.Id))).Select(x => x.Id));
                }
                if (e is Grid) TouchesDatum = true;
            }

            private void ProximityRelation(Element e)
            {
                var box = e.get_BoundingBox(null);
                if (box == null || e.ViewSpecific) return;
                var pad = new XYZ(_paddingFeet, _paddingFeet, _paddingFeet);
                var outline = new Autodesk.Revit.DB.Outline(box.Min - pad, box.Max + pad);
                Add("proximity", Scan(new FilteredElementCollector(_doc).WhereElementIsNotElementType()
                        .WherePasses(new BoundingBoxIntersectsFilter(outline)))
                    .Where(x => !x.ViewSpecific && x.Category != null && x.Category.CategoryType == CategoryType.Model)
                    .Select(x => x.Id));
            }

            private void AnnotationRelation()
            {
                foreach (var tag in Scan(new FilteredElementCollector(_doc).OfClass(typeof(IndependentTag))).Cast<IndependentTag>())
                    if (tag.GetTaggedLocalElementIds().Any(Targets.ContainsKey)) Add("annotations", new[] { tag.Id });
                foreach (var dimension in Scan(new FilteredElementCollector(_doc).OfClass(typeof(Dimension))).Cast<Dimension>())
                {
                    var references = dimension.References;
                    if (references == null) continue;
                    foreach (Reference reference in references)
                        if (Targets.ContainsKey(reference.ElementId))
                        {
                            Add("annotations", new[] { dimension.Id });
                            break;
                        }
                }
            }

            private sealed class ScanLimitException : Exception
            {
            }
        }
    }
}
