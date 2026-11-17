// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Mep/AnalyzeMepNetworkHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: the system lookup lists the valid systems on a miss or ambiguity; is_well_connected is null for
// electrical systems (rvt-mcp reported true, so electrical systems never got the "not well connected" issue either
// way); open connectors are listed (up to 200, with element and connector ids) besides being counted; IDs are numbers;
// errors are "CODE: message".

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Mep
{
    public sealed class AnalyzeMepNetworkHandler : IRevitCommand
    {
        private const int MaxListedOpenConnectors = 200;

        public string Name => "analyze_mep_network";
        public string Description =>
            "Check one MEP system's health (system_id or system_name): element count and category breakdown of the pipe or duct network "
            + "(circuit members for electrical), terminal count (without base equipment), base equipment, whether Revit sees it as well "
            + "connected, open End connectors in the system's domain (counted, first 200 listed), plus issues and recommendations.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""system_id"":{""type"":""integer""},
            ""system_name"":{""type"":""string""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!MepHelpers.TrySystem(doc, p.Value<long?>("system_id"), p.Value<string>("system_name"), out var system, out var failure))
                return failure;

            var domain = MepHelpers.DomainOf(system);
            MepSystemMembership snapshot;
            try
            {
                snapshot = MepSystemMembership.Read(system);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("READ_FAILED: " + ex.Message);
            }

            var expected = domain == "piping" ? Domain.DomainPiping : domain == "mechanical" ? Domain.DomainHvac : Domain.DomainElectrical;
            var breakdown = new Dictionary<string, int>(StringComparer.Ordinal);
            var openCount = 0;
            var openList = new JArray();
            foreach (var member in snapshot.FlowElements)
            {
                var category = member.Category?.Name ?? "Uncategorized";
                breakdown[category] = breakdown.TryGetValue(category, out var n) ? n + 1 : 1;
                var manager = MepHelpers.GetConnectorManager(member);
                if (manager == null) continue;
                foreach (Connector connector in manager.Connectors)
                {
                    try
                    {
                        // Equipment can carry connectors of several disciplines: count only this system's domain.
                        if (connector.ConnectorType != ConnectorType.End || connector.Domain != expected || connector.IsConnected) continue;
                        openCount++;
                        if (openList.Count < MaxListedOpenConnectors)
                            openList.Add(new JObject
                            {
                                ["element_id"] = RevitCompat.GetId(member.Id),
                                ["connector_id"] = connector.Id,
                                ["origin_mm"] = MepHelpers.PointMm(MepHelpers.SafeOrigin(connector))
                            });
                    }
                    catch
                    {
                        // One unreadable connector must not abort the count.
                    }
                }
            }

            var wellConnected = MepHelpers.IsWellConnected(system);
            var counts = snapshot.Counts;
            var issues = new List<string>();
            var recommendations = new List<string>();
            if (wellConnected == false)
            {
                issues.Add("System is not well connected.");
                recommendations.Add("Reconnect open connectors and disconnected segments so every element traces back to the source equipment.");
            }
            if (openCount > 0)
            {
                issues.Add(openCount + " open connector" + (openCount == 1 ? "" : "s") + " detected.");
                recommendations.Add("Cap, connect or terminate the open connector" + (openCount == 1 ? "" : "s") + " to complete the network.");
            }
            if (snapshot.BaseEquipment == null)
            {
                issues.Add("No base equipment assigned.");
                recommendations.Add("Assign source equipment to the system so flow and pressure calculations resolve.");
            }
            if (counts.IsEmpty)
            {
                issues.Add("System has no member elements.");
                recommendations.Add("Assign elements to this system or delete it if unused.");
            }
            if (issues.Count == 0) recommendations.Add("No topology issues detected.");

            return CommandResult.Ok(new
            {
                system_id = RevitCompat.GetId(system.Id),
                system_name = MepHelpers.SafeName(system) ?? "",
                domain,
                system_type = MepHelpers.SystemTypeName(doc, system),
                is_well_connected = wellConnected,
                element_count = counts.ElementCount,
                terminal_count = counts.TerminalCount,
                category_breakdown = breakdown.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
                base_equipment = snapshot.BaseEquipment == null ? null : new
                {
                    id = RevitCompat.GetId(snapshot.BaseEquipment.Id),
                    name = MepHelpers.SafeName(snapshot.BaseEquipment)
                },
                open_connector_count = openCount,
                open_connectors = openList,
                open_connectors_truncated = openCount > openList.Count,
                issues,
                recommendations
            });
        }
    }
}
