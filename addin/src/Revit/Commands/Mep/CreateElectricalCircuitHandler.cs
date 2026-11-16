// New in B2M (Step 10, F5): puts electrical devices and fixtures (sockets, switches, lights, fans, AC units) on one
// circuit (ElectricalSystem.Create) and, when a panel is given, assigns it (SelectPanel). Every element is checked first:
// one without a free electrical connector of the circuit's type is an error naming it, and nothing is created.
// One transaction: if Revit refuses the circuit or the panel (a voltage or distribution system mismatch), it all rolls back.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Mep
{
    public sealed class CreateElectricalCircuitHandler : IRevitCommand
    {
        private const int MaxElements = 200;

        public string Name => "create_electrical_circuit";
        public string Description =>
            "Create one electrical circuit from devices and fixtures (element_ids) and, with panel_id, assign it to that panel "
            + "(electrical equipment). system_type is the circuit type (default PowerCircuit; others such as Data, FireAlarm, "
            + "Communication, Controls). Each element needs a free electrical connector of that type; otherwise the error lists the "
            + "elements and why, and nothing is created. Returns circuit_id, circuit_number, panel and created_ids.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""panel_id"":{""type"":""integer""},
            ""system_type"":{""type"":""string""}},
            ""required"":[""element_ids""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var typeName = p.Value<string>("system_type") ?? "PowerCircuit";
            if (!Enum.TryParse(typeName, true, out ElectricalSystemType systemType) || int.TryParse(typeName, out _))
                return CommandResult.Fail("INVALID_PARAMS: system_type '" + typeName + "' is not a circuit type.",
                    new JObject { ["valid_system_types"] = new JArray(Enum.GetNames(typeof(ElectricalSystemType)).Cast<object>().ToArray()) });

            var ids = (p["element_ids"] as JArray)?.Select(t => t.Value<long>()).Distinct().ToList() ?? new List<long>();
            if (ids.Count == 0 || ids.Count > MaxElements)
                return CommandResult.Fail("INVALID_PARAMS: element_ids lists 1 to " + MaxElements + " devices or fixtures.");

            var problems = new JArray();
            foreach (var id in ids)
            {
                var reason = Check(doc, id, systemType);
                if (reason != null) problems.Add(new JObject { ["element_id"] = id, ["reason"] = reason });
            }
            if (problems.Count > 0)
                return CommandResult.Fail("NO_ELECTRICAL_CONNECTOR: " + problems.Count + " of the elements cannot join a " + systemType
                    + " circuit (" + string.Join("; ", problems.Select(x => x.Value<long>("element_id") + ": " + x.Value<string>("reason"))) + "). Nothing was created.",
                    new JObject { ["problems"] = problems });

            FamilyInstance panel = null;
            var panelId = p.Value<long?>("panel_id");
            if (panelId.HasValue)
            {
                panel = doc.GetElement(RevitCompat.ToElementId(panelId.Value)) as FamilyInstance;
                if (panel == null)
                    return CommandResult.Fail("ELEMENT_NOT_FOUND: panel " + panelId.Value + " is not a family instance in this document.");
                if (panel.Category?.BuiltInCategory != BuiltInCategory.OST_ElectricalEquipment)
                    return CommandResult.Fail("NOT_A_PANEL: element " + panelId.Value + " is a " + (panel.Category?.Name ?? "element")
                        + ", not electrical equipment.");
            }

            using (var tx = new Transaction(doc, "B2M: Create circuit"))
            {
                tx.Start();
                try
                {
                    var circuit = ElectricalSystem.Create(doc, ids.Select(RevitCompat.ToElementId).ToList(), systemType)
                        ?? throw new InvalidOperationException("Revit returned no circuit.");
                    if (panel != null)
                    {
                        circuit.SelectPanel(panel);
                        doc.Regenerate();
                        if (circuit.BaseEquipment == null || circuit.BaseEquipment.Id != panel.Id)
                            throw new InvalidOperationException("Revit did not assign the circuit to panel " + panelId.Value + ".");
                    }
                    var members = circuit.Elements.Cast<Element>().Select(e => RevitCompat.GetId(e.Id)).ToList();
                    var missing = ids.Where(id => !members.Contains(id)).ToList();
                    if (missing.Count > 0)
                        throw new InvalidOperationException("Revit left out " + string.Join(", ", missing) + ".");

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures. Nothing was created.");

                    return CommandResult.Ok(new
                    {
                        circuit_id = RevitCompat.GetId(circuit.Id),
                        circuit_number = circuit.CircuitNumber,
                        load_name = circuit.LoadName,
                        system_type = systemType.ToString(),
                        panel_id = circuit.BaseEquipment == null ? (long?)null : RevitCompat.GetId(circuit.BaseEquipment.Id),
                        panel_name = circuit.PanelName,
                        element_ids = members,
                        created_ids = new[] { RevitCompat.GetId(circuit.Id) }
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the circuit" + (panel == null ? "" : " on panel " + panelId.Value)
                        + ": " + ex.Message + " Nothing was created.");
                }
            }
        }

        /// <summary>Why the element cannot join a circuit of this type, or null when it has a free connector for one.</summary>
        private static string Check(Document doc, long id, ElectricalSystemType systemType)
        {
            var element = doc.GetElement(RevitCompat.ToElementId(id));
            if (element == null) return "not in this document";
            var manager = MepHelpers.GetConnectorManager(element);
            if (!(element is FamilyInstance) || manager == null) return "a " + (element.Category?.Name ?? element.GetType().Name) + " with no electrical connector";
            var electrical = manager.Connectors.Cast<Connector>().Where(c => c.Domain == Domain.DomainElectrical).ToList();
            if (electrical.Count == 0) return "its family (" + MepHelpers.TypeName(doc, element) + ") has no electrical connector";
            var ofType = electrical.Where(c => SafeType(c) == systemType).ToList();
            if (ofType.Count == 0)
                return "no " + systemType + " connector (it has " + string.Join(", ", electrical.Select(c => SafeType(c)?.ToString() ?? "unknown").Distinct()) + ")";
            if (ofType.All(c => c.MEPSystem != null))
                return "already on circuit " + string.Join(", ", ofType.Select(c => RevitCompat.GetId(c.MEPSystem.Id)).Distinct());
            return null;
        }

        private static ElectricalSystemType? SafeType(Connector c)
        {
            try { return c.ElectricalSystemType; }
            catch { return null; }
        }
    }
}
