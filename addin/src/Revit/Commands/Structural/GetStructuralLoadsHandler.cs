// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Structural/GetStructuralLoadsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: loads are read through the PointLoad/LineLoad/AreaLoad API instead of LOAD_FORCE_* parameters (which
// line and area loads do not carry) and converted from Revit's internal units to kN, kN/m, kN/m2 and kN*m (rvt-mcp
// returned raw internal values); line loads report both ends, area loads their three corner vectors; truncated is
// true only when loads were left out (rvt-mcp also set it when exactly limit loads existed); element_id is checked.

using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Structural
{
    public sealed class GetStructuralLoadsHandler : IRevitCommand
    {
        public string Name => "get_structural_loads";
        public string Description =>
            "List structural loads: point (kN, kN*m), line (kN/m, kN*m/m, at both ends) and area (kN/m2, three corner vectors), "
            + "with host element and load case. Optional element_id (host) and load_type (point, line, area); limit (default and max 500).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_id"":{""type"":""integer""},
            ""load_type"":{""type"":""string"",""enum"":[""point"",""line"",""area""]},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var limit = p.Value<int?>("limit") ?? 500;
            if (limit < 1 || limit > 500) return CommandResult.Fail("INVALID_PARAMS: limit must be between 1 and 500.");
            var hostId = p.Value<long?>("element_id");
            if (hostId.HasValue && doc.GetElement(RevitCompat.ToElementId(hostId.Value)) == null)
                return CommandResult.Fail("ELEMENT_NOT_FOUND: no element has id " + hostId.Value + ".");
            var loadType = p.Value<string>("load_type");

            var classes = new List<System.Type>();
            if (loadType == null || loadType == "point") classes.Add(typeof(PointLoad));
            if (loadType == null || loadType == "line") classes.Add(typeof(LineLoad));
            if (loadType == null || loadType == "area") classes.Add(typeof(AreaLoad));

            var matched = 0;
            var rows = new JArray();
            foreach (var cls in classes)
            {
                foreach (var load in new FilteredElementCollector(doc).OfClass(cls).Cast<LoadBase>())
                {
                    var host = load.HostElementId;
                    if (hostId.HasValue && (host == null || RevitCompat.GetId(host) != hostId.Value)) continue;
                    matched++;
                    if (rows.Count >= limit) continue;
                    rows.Add(Describe(doc, load));
                }
            }

            return CommandResult.Ok(new
            {
                count = rows.Count,
                total_matched = matched,
                truncated = matched > rows.Count,
                items = rows
            });
        }

        private static JObject Describe(Document doc, LoadBase load)
        {
            var row = new JObject
            {
                ["id"] = RevitCompat.GetId(load.Id),
                ["host_id"] = load.HostElementId == null || load.HostElementId == ElementId.InvalidElementId ? null : (JToken)RevitCompat.GetId(load.HostElementId),
                ["is_hosted"] = load.IsHosted,
                ["case_id"] = RevitCompat.GetIdOrNull(load.LoadCaseId),
                ["case_name"] = load.LoadCaseName
            };
            switch (load)
            {
                case PointLoad point:
                    row["load_kind"] = "point";
                    row["point_mm"] = StructuralHelpers.PointMm(point.Point);
                    row["force_kn"] = Vector(point.ForceVector, UnitTypeId.Kilonewtons);
                    row["moment_kn_m"] = Vector(point.MomentVector, UnitTypeId.KilonewtonMeters);
                    break;
                case LineLoad line:
                    row["load_kind"] = "line";
                    row["start_mm"] = StructuralHelpers.PointMm(line.StartPoint);
                    row["end_mm"] = StructuralHelpers.PointMm(line.EndPoint);
                    row["force_kn_per_m_start"] = Vector(line.ForceVector1, UnitTypeId.KilonewtonsPerMeter);
                    row["force_kn_per_m_end"] = Vector(line.ForceVector2, UnitTypeId.KilonewtonsPerMeter);
                    row["moment_kn_m_per_m_start"] = Vector(line.MomentVector1, UnitTypeId.KilonewtonMetersPerMeter);
                    row["moment_kn_m_per_m_end"] = Vector(line.MomentVector2, UnitTypeId.KilonewtonMetersPerMeter);
                    break;
                case AreaLoad area:
                    row["load_kind"] = "area";
                    var vectors = new JArray();
                    // A uniform area load has one reference point; a varying one has three.
                    var count = area.NumRefPoints >= 3 ? 3 : 1;
                    for (var i = 0; i < count; i++)
                        vectors.Add(Vector(ForceAt(area, i), UnitTypeId.KilonewtonsPerSquareMeter));
                    row["force_kn_per_m2"] = vectors;
                    row["area_m2"] = System.Math.Round(UnitUtils.ConvertFromInternalUnits(area.Area, UnitTypeId.SquareMeters), 3);
                    break;
                default:
                    row["load_kind"] = "unknown";
                    break;
            }
            return row;
        }

        private static XYZ ForceAt(AreaLoad area, int i) =>
            i == 0 ? area.ForceVector1 : i == 1 ? area.ForceVector2 : area.ForceVector3;

        private static JToken Vector(XYZ v, ForgeTypeId unit) =>
            v == null ? null : new JArray(Convert(v.X, unit), Convert(v.Y, unit), Convert(v.Z, unit));

        private static double Convert(double value, ForgeTypeId unit) =>
            System.Math.Round(UnitUtils.ConvertFromInternalUnits(value, unit), 4);
    }
}
