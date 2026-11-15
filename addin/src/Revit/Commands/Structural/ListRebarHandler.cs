// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Structural/ListRebarHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: the bar diameter is read from RebarBarType.BarModelDiameter (Revit 2024+) instead of trying property
// names by reflection; host_id and view_id are checked (a wrong id was an empty list); the host filter runs in the
// collector loop; each rebar adds its bar type name and shape; diameter via Mm.

using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Structural
{
    using View = Autodesk.Revit.DB.View;

    public sealed class ListRebarHandler : IRevitCommand
    {
        public string Name => "list_rebar";
        public string Description =>
            "List rebar: id, bar type and diameter (mm), shape, quantity, layout rule and host. Optional host_id and view_id filters; "
            + "limit caps the list (default and max 500).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""host_id"":{""type"":""integer""},
            ""view_id"":{""type"":""integer""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var limit = p.Value<int?>("limit") ?? 500;
            if (limit < 1 || limit > 500) return CommandResult.Fail("INVALID_PARAMS: limit must be between 1 and 500.");
            CommandResult failure;
            View view = null;
            var viewId = p.Value<long?>("view_id");
            if (viewId.HasValue && !Lookup.TryView(doc, viewId.Value, out view, out failure)) return failure;
            var hostId = p.Value<long?>("host_id");
            if (hostId.HasValue && doc.GetElement(RevitCompat.ToElementId(hostId.Value)) == null)
                return CommandResult.Fail("ELEMENT_NOT_FOUND: no element has id " + hostId.Value + ".");

            var collector = view == null ? new FilteredElementCollector(doc) : new FilteredElementCollector(doc, view.Id);
            var matched = 0;
            var rows = new JArray();
            foreach (var rebar in collector.OfClass(typeof(Rebar)).Cast<Rebar>())
            {
                var host = rebar.GetHostId();
                if (hostId.HasValue && RevitCompat.GetId(host) != hostId.Value) continue;
                matched++;
                if (rows.Count >= limit) continue;
                var barType = doc.GetElement(rebar.GetTypeId()) as RebarBarType;
                var hostElement = doc.GetElement(host);
                var shape = doc.GetElement(rebar.GetShapeId());
                rows.Add(new JObject
                {
                    ["id"] = RevitCompat.GetId(rebar.Id),
                    ["bar_type_id"] = RevitCompat.GetIdOrNull(rebar.GetTypeId()),
                    ["bar_type_name"] = barType?.Name,
                    ["diameter_mm"] = barType == null ? null : (JToken)Mm.FromFeet(barType.BarModelDiameter),
                    ["shape"] = shape?.Name,
                    ["quantity"] = rebar.Quantity,
                    ["layout_rule"] = rebar.LayoutRule.ToString(),
                    ["host_id"] = RevitCompat.GetIdOrNull(host),
                    ["host_category"] = hostElement?.Category?.Name
                });
            }

            return CommandResult.Ok(new
            {
                count = rows.Count,
                total_matched = matched,
                truncated = matched > rows.Count,
                items = rows
            });
        }
    }
}
