// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Elements/GetElementDetailsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: mm through Mm (no literal 304.8); WorksetId read without the 2026-removed ElementId.IntegerValue;
// adds builtInCategory and, for curve locations, the curve length; at most 1000 IDs per call; the per-property
// try/catch wrappers are folded into one per element.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Query
{
    public sealed class GetElementDetailsHandler : IRevitCommand
    {
        private const int MaxIds = 1000;

        public string Name => "get_element_details";
        public string Description => "Metadata, location and bounding box (mm) of elements by ID: type, level, group, assembly, workset, phases, "
            + "design option, owner view, pinned.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}}},""required"":[""elementIds""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            var ids = p["elementIds"].ToObject<long[]>().Distinct().ToArray();
            if (ids.Length == 0 || ids.Length > MaxIds) return CommandResult.Fail("INVALID_PARAMS: give 1 to " + MaxIds + " elementIds.");

            var elements = new JArray();
            var errors = new JArray();
            foreach (var id in ids)
            {
                try
                {
                    var element = doc.GetElement(RevitCompat.ToElementId(id));
                    if (element == null) errors.Add(new JObject { ["elementId"] = id, ["error"] = "not found" });
                    else elements.Add(Details(doc, element));
                }
                catch (Exception ex)
                {
                    errors.Add(new JObject { ["elementId"] = id, ["error"] = ex.Message });
                }
            }
            return CommandResult.Ok(new JObject
            {
                ["requested"] = ids.Length,
                ["returned"] = elements.Count,
                ["elements"] = elements,
                ["errors"] = errors
            });
        }

        private static JObject Details(Document doc, Element e)
        {
            string worksetName = null;
            var worksetId = QueryHelpers.WorksetIdOf(e);
            if (worksetId.HasValue && doc.IsWorkshared) worksetName = doc.GetWorksetTable().GetWorkset(new WorksetId(worksetId.Value))?.Name;
            var box = e.get_BoundingBox(null);
            return new JObject
            {
                ["elementId"] = RevitCompat.GetId(e.Id),
                ["uniqueId"] = e.UniqueId,
                ["name"] = QueryHelpers.NameOf(e),
                ["category"] = e.Category?.Name,
                ["builtInCategory"] = Lookup.BuiltInName(e.Category),
                ["typeId"] = QueryHelpers.Id(e.GetTypeId()),
                ["typeName"] = QueryHelpers.NameOf(doc.GetElement(e.GetTypeId())),
                ["levelId"] = QueryHelpers.Id(e.LevelId),
                ["levelName"] = QueryHelpers.NameOf(doc.GetElement(e.LevelId)),
                ["groupId"] = QueryHelpers.Id(e.GroupId),
                ["assemblyInstanceId"] = QueryHelpers.Id(e.AssemblyInstanceId),
                ["worksetId"] = worksetId,
                ["worksetName"] = worksetName,
                ["phaseCreated"] = Named(doc, e.CreatedPhaseId),
                ["phaseDemolished"] = Named(doc, e.DemolishedPhaseId),
                ["pinned"] = e.Pinned,
                ["designOptionId"] = QueryHelpers.Id(e.DesignOption?.Id),
                ["designOptionName"] = e.DesignOption?.Name,
                ["ownerViewId"] = QueryHelpers.Id(e.OwnerViewId),
                ["ownerViewName"] = QueryHelpers.NameOf(doc.GetElement(e.OwnerViewId)),
                ["location"] = Location(e),
                ["boundingBox"] = box == null ? null : new JObject
                {
                    ["unit"] = "mm",
                    ["min"] = QueryHelpers.Point(box.Min),
                    ["max"] = QueryHelpers.Point(box.Max)
                }
            };
        }

        private static JObject Named(Document doc, ElementId id) =>
            QueryHelpers.IdOrNull(id) is long value ? new JObject { ["id"] = value, ["name"] = QueryHelpers.NameOf(doc.GetElement(id)) } : null;

        private static JObject Location(Element e)
        {
            switch (e.Location)
            {
                case null:
                    return null;
                case LocationPoint point:
                    double? rotation = null;
                    try
                    {
                        rotation = Math.Round(point.Rotation * 180 / Math.PI, 6);
                    }
                    catch
                    {
                        // Rooms and groups have a point without a rotation.
                    }
                    return new JObject { ["type"] = "point", ["unit"] = "mm", ["point"] = QueryHelpers.Point(point.Point), ["rotationDegrees"] = rotation };
                case LocationCurve curve when curve.Curve != null:
                    var c = curve.Curve;
                    return new JObject
                    {
                        ["type"] = "curve",
                        ["unit"] = "mm",
                        ["start"] = c.IsBound ? QueryHelpers.Point(c.GetEndPoint(0)) : null,
                        ["end"] = c.IsBound ? QueryHelpers.Point(c.GetEndPoint(1)) : null,
                        ["length"] = Mm.FromFeet(c.Length, 3)
                    };
                default:
                    return new JObject { ["type"] = e.Location.GetType().Name };
            }
        }
    }
}
