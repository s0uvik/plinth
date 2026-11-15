// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Families/GetFamilyInstancesHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: mm conversion through Mm (no local 304.8); family_id is an integer and ids are returned as
// integers; misses list the valid families or the family's type names; the Mark is read from the built-in
// parameter so it works in every Revit language.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Families
{
    public sealed class GetFamilyInstancesHandler : IRevitCommand
    {
        public string Name => "get_family_instances";
        public string Description =>
            "List placed instances of a family (family_id or family_name), optionally of one type_name and only in the active view: "
            + "type, level, host, Mark and location in mm (point, or line start/end).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + FamiliesHelpers.FamilySchema + @",
            ""type_name"":{""type"":""string""},
            ""view_only"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!FamiliesHelpers.TryFamily(doc, p.Value<long?>("family_id"), p.Value<string>("family_name"), out var family, out var failure))
                return failure;

            var typeName = p.Value<string>("type_name");
            var viewOnly = p.Value<bool?>("view_only") ?? false;
            var limit = p.Value<int?>("limit") ?? 1000;
            if (limit < 1 || limit > 10000) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to 10000.");

            var symbols = family.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>().ToList();
            var wanted = string.IsNullOrEmpty(typeName)
                ? symbols
                : symbols.Where(s => string.Equals(s.Name, typeName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (wanted.Count == 0 && !string.IsNullOrEmpty(typeName))
                return CommandResult.Fail("TYPE_NOT_FOUND: family '" + family.Name + "' has no type named '" + typeName + "'.",
                    new JObject { ["valid_types"] = new JArray(symbols.Select(s => s.Name).OrderBy(n => n)) });
            var ids = new HashSet<long>(wanted.Select(s => RevitCompat.GetId(s.Id)));

            FilteredElementCollector collector;
            if (viewOnly)
            {
                if (doc.ActiveView == null) return CommandResult.Fail("NO_ACTIVE_VIEW: view_only needs an active view.");
                collector = new FilteredElementCollector(doc, doc.ActiveView.Id);
            }
            else
            {
                collector = new FilteredElementCollector(doc);
            }
            var matches = collector.OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType().Cast<FamilyInstance>()
                .Where(fi => ids.Contains(RevitCompat.GetId(fi.GetTypeId()))).ToList();

            return CommandResult.Ok(new JObject
            {
                ["family_id"] = RevitCompat.GetId(family.Id),
                ["family_name"] = family.Name,
                ["category"] = family.FamilyCategory?.Name,
                ["type_filter"] = string.IsNullOrEmpty(typeName) ? null : typeName,
                ["view_only"] = viewOnly,
                ["total_instances"] = matches.Count,
                ["returned"] = Math.Min(limit, matches.Count),
                ["limit_hit"] = matches.Count > limit,
                ["instances"] = new JArray(matches.Take(limit).Select(fi => Describe(doc, fi)))
            });
        }

        private static JObject Describe(Document doc, FamilyInstance fi)
        {
            var o = new JObject
            {
                ["id"] = RevitCompat.GetId(fi.Id),
                ["type_id"] = RevitCompat.GetId(fi.GetTypeId()),
                ["type_name"] = fi.Symbol?.Name
            };
            try
            {
                var levelId = fi.LevelId;
                if ((levelId == null || levelId == ElementId.InvalidElementId) && fi.Host != null) levelId = fi.Host.LevelId;
                var level = levelId == null || levelId == ElementId.InvalidElementId ? null : doc.GetElement(levelId);
                o["level_id"] = level == null ? null : (JToken)RevitCompat.GetId(level.Id);
                o["level_name"] = level?.Name;
                o["host_id"] = fi.Host == null ? null : (JToken)RevitCompat.GetId(fi.Host.Id);
                o["host_name"] = fi.Host?.Name;
                o["mark"] = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                switch (fi.Location)
                {
                    case LocationPoint lp:
                        o["location_kind"] = "point";
                        o["location_point_mm"] = new JArray(FamiliesHelpers.PointMm(lp.Point));
                        break;
                    case LocationCurve lc when lc.Curve != null && lc.Curve.IsBound:
                        o["location_kind"] = "line";
                        o["location_line_start_mm"] = new JArray(FamiliesHelpers.PointMm(lc.Curve.GetEndPoint(0)));
                        o["location_line_end_mm"] = new JArray(FamiliesHelpers.PointMm(lc.Curve.GetEndPoint(1)));
                        break;
                    default:
                        o["location_kind"] = null;
                        break;
                }
            }
            catch (Exception ex)
            {
                o["error"] = ex.Message;
            }
            return o;
        }
    }
}
