// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Mep/GetMepElementConnectorsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: each connector also reports its MEP system (system_id, system_name), which replaces the
// read-only report mode dropped from set_system_classification; flow is converted to litres per second
// (rvt-mcp returned Revit's internal cubic feet per second); an element without connectors is an error
// (rvt-mcp returned an empty list); connector type (End, Curve, Logical) is reported; IDs are numbers.

using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Mep
{
    public sealed class GetMepElementConnectorsHandler : IRevitCommand
    {
        public string Name => "get_mep_element_connectors";
        public string Description =>
            "List the connectors of an MEP element (duct, pipe, cable tray, conduit, fitting, equipment, terminal): connector_id "
            + "(stable; pass it to connect_mep_elements and create_mep_fitting), ordinal, type, domain, shape, origin in mm, connection "
            + "status and connected element IDs, size in mm (radius or width/height), flow in L/s, direction and system.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""element_id"":{""type"":""integer""}},""required"":[""element_id""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var id = p.Value<long>("element_id");
            var element = doc.GetElement(RevitCompat.ToElementId(id));
            if (element == null) return CommandResult.Fail("ELEMENT_NOT_FOUND: no element has id " + id + ".");
            var manager = MepHelpers.GetConnectorManager(element);
            if (manager == null) return CommandResult.Fail("NO_CONNECTORS: element " + id + " has no MEP connectors.");

            var ownerId = RevitCompat.GetId(element.Id);
            var connectors = new JArray();
            var ordinal = 0;
            foreach (Connector connector in manager.Connectors)
            {
                connectors.Add(Describe(connector, ordinal, ownerId));
                ordinal++;
            }

            return CommandResult.Ok(new
            {
                element_id = ownerId,
                element_category = element.Category?.Name,
                built_in_category = Lookup.BuiltInName(element.Category),
                connector_count = connectors.Count,
                connectors
            });
        }

        // Each read is guarded: connector properties throw when they do not apply (Radius on a rectangular port,
        // Flow on an electrical one, Origin on a logical one). One bad property must not lose the connector.
        private static JObject Describe(Connector connector, int ordinal, long ownerId)
        {
            var row = new JObject { ["ordinal"] = ordinal };
            Try(() => row["connector_id"] = connector.Id);
            Try(() => row["connector_type"] = connector.ConnectorType.ToString());
            Try(() => row["domain"] = MepHelpers.DomainName(connector.Domain));
            var shape = ConnectorProfileType.Invalid;
            Try(() =>
            {
                shape = connector.Shape;
                row["shape"] = shape.ToString();
            });
            row["origin_mm"] = MepHelpers.PointMm(MepHelpers.SafeOrigin(connector));
            Try(() => row["is_connected"] = connector.IsConnected);

            var connectedTo = new List<long>();
            Try(() =>
            {
                foreach (Connector other in connector.AllRefs)
                {
                    var otherId = other.Owner == null ? 0 : RevitCompat.GetId(other.Owner.Id);
                    if (otherId <= 0 || otherId == ownerId || connectedTo.Contains(otherId)) continue; // Skip logical refs and self.
                    connectedTo.Add(otherId);
                }
            });
            row["connected_to_element_ids"] = new JArray(connectedTo);

            if (shape == ConnectorProfileType.Round || shape == ConnectorProfileType.Invalid)
                Try(() => row["radius_mm"] = Mm.FromFeet(connector.Radius));
            if (shape == ConnectorProfileType.Rectangular || shape == ConnectorProfileType.Oval || shape == ConnectorProfileType.Invalid)
            {
                Try(() => row["width_mm"] = Mm.FromFeet(connector.Width));
                Try(() => row["height_mm"] = Mm.FromFeet(connector.Height));
            }
            Try(() =>
            {
                if (connector.Domain == Domain.DomainHvac || connector.Domain == Domain.DomainPiping)
                    row["flow_l_per_s"] = System.Math.Round(UnitUtils.ConvertFromInternalUnits(connector.Flow, UnitTypeId.LitersPerSecond), 4);
            });
            Try(() => row["direction"] = connector.Direction.ToString());
            Try(() =>
            {
                var system = connector.MEPSystem;
                if (system == null) return;
                row["system_id"] = RevitCompat.GetId(system.Id);
                row["system_name"] = system.Name;
            });
            return row;
        }

        private static void Try(System.Action read)
        {
            try { read(); }
            catch { /* property does not apply to this connector */ }
        }
    }
}
