// New in Plinth (Step 10, F5): puts electrical devices and fixtures (sockets, switches, lights, fans, AC units) on one
// circuit (ElectricalSystem.Create) and, when a panel is given, assigns it (SelectPanel). Every element is checked first:
// one without a free electrical connector of the circuit's type is an error naming it, and nothing is created.
// One transaction: if Revit refuses the circuit or the panel (a voltage or distribution system mismatch), it all rolls back.
// best_effort (2026-11-27, MEP fit-out on real-world families: an indicative model where a socket family without a power
// connector, or a panel of the wrong voltage, should not stop the rest): elements without a free connector of the type are
// left out and listed in skipped (none left: success with circuit_id null and nothing changed); the circuit is committed
// in its own transaction first and the panel assigned in a second one, so a panel Revit refuses (SelectPanel throws, or
// the FailureScope rolls the assignment back on a warning or error) only undoes the assignment: the circuit is kept
// without a panel and panel_error says why. A SubTransaction would not do: failures are processed when the outer
// transaction commits, and the FailureScope's rollback there would take the circuit with it. Both transactions sit in one
// TransactionGroup, so it is one undo step.
// Connector types (2026-11-27, after a Revit API review): real sockets and fixtures carry PowerBalanced or PowerUnBalanced
// connectors (the family editor offers no other power type), so a PowerCircuit takes those as well as PowerCircuit; checking
// for an exact PowerCircuit refused every real socket. A free connector whose type is undefined or cannot be read (Revit
// documents an unconnected connector's type as possibly undefined) is not refused beforehand: ElectricalSystem.Create and
// the membership check after it decide. system_type takes only the eight circuit types (PowerBalanced, PowerUnBalanced and
// UndefinedSystemType are connector types). Both rules are in Core (CircuitTypes), unit-tested. With best_effort, when
// every element passes the check but Create still throws ArgumentException (Revit's "at least one component that can
// create the specified circuit type"), the elements are skipped with Revit's message and the result is the same
// circuit-less success as when none passes; the TransactionGroup is rolled back, so nothing is left in the document.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Plinth.Revit.Core.Mep;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Mep
{
    public sealed class CreateElectricalCircuitHandler : IRevitCommand
    {
        private const int MaxElements = 200;

        public string Name => "create_electrical_circuit";
        public string Description =>
            "Create one electrical circuit from devices and fixtures (element_ids) and, with panel_id, assign it to that panel "
            + "(electrical equipment). system_type is the circuit type: PowerCircuit (default), Data, Communication, Controls, "
            + "FireAlarm, NurseCall, Security or Telephone (anything else is INVALID_PARAMS listing these). Each element needs a free "
            + "electrical connector that can join it: a PowerCircuit takes PowerBalanced, PowerUnBalanced or PowerCircuit connectors, "
            + "another type a connector of the same type; a free connector whose type Revit reports as undefined is left to Revit, "
            + "and an element Revit leaves out of the circuit is an error. Otherwise the error lists the elements and why, and "
            + "nothing is created. Returns circuit_id, circuit_number, panel and created_ids. best_effort true: elements that cannot "
            + "join are left out and listed in skipped [{element_id, reason}] (none left, or Revit refuses them all for this circuit "
            + "type: success with circuit_id null, element_ids [], created_ids [], panel_id null, a note, and nothing changed), and a "
            + "panel Revit refuses (voltage, poles, no free slot) leaves the circuit without a panel and returns panel_error (null "
            + "when assigned).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""panel_id"":{""type"":""integer""},
            ""system_type"":{""type"":""string"",""description"":""the circuit type: PowerCircuit (default), Data, Communication, Controls, FireAlarm, NurseCall, Security or Telephone""},
            ""best_effort"":{""type"":""boolean"",""description"":""skip elements that cannot join and keep the circuit if the panel is refused (default false)""}},
            ""required"":[""element_ids""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var typeError = CircuitTypes.Error(p.Value<string>("system_type"), out var typeName);
            if (typeError != null)
                return CommandResult.Fail("INVALID_PARAMS: " + typeError + ".",
                    new JObject { ["valid_system_types"] = new JArray(CircuitTypes.Valid.Cast<object>().ToArray()) });
            if (!Enum.TryParse(typeName, false, out ElectricalSystemType systemType))
                return CommandResult.Fail("INVALID_PARAMS: this Revit has no circuit type " + typeName + ".");

            var ids = (p["element_ids"] as JArray)?.Select(t => t.Value<long>()).Distinct().ToList() ?? new List<long>();
            if (ids.Count == 0 || ids.Count > MaxElements)
                return CommandResult.Fail("INVALID_PARAMS: element_ids lists 1 to " + MaxElements + " devices or fixtures.");

            var bestEffort = p.Value<bool?>("best_effort") ?? false;
            var problems = new JArray();
            foreach (var id in ids)
            {
                var reason = Check(doc, id, systemType);
                if (reason != null) problems.Add(new JObject { ["element_id"] = id, ["reason"] = reason });
            }
            if (problems.Count > 0 && !bestEffort)
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

            if (bestEffort)
            {
                var skippedIds = new HashSet<long>(problems.Select(x => x.Value<long>("element_id")));
                return BestEffort(doc, ids.Where(id => !skippedIds.Contains(id)).ToList(), systemType, panel, problems);
            }

            using (var tx = new Transaction(doc, "Plinth: Create circuit"))
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

        /// <summary>
        /// best_effort: the circuit from the elements that can join, committed on its own; then the panel in a second
        /// transaction, whose failure only undoes the assignment. One TransactionGroup, so one undo step.
        /// </summary>
        private static CommandResult BestEffort(Document doc, List<long> ids, ElectricalSystemType systemType, FamilyInstance panel, JArray skipped)
        {
            if (ids.Count == 0)
                return NoCircuit(systemType, skipped, "No element can join a " + systemType + " circuit; nothing was created.");

            using (var group = new TransactionGroup(doc, "Plinth: Create circuit"))
            {
                group.Start();
                ElectricalSystem circuit;
                List<long> members;
                using (var tx = new Transaction(doc, "Plinth: Create circuit"))
                {
                    tx.Start();
                    try
                    {
                        // Every element passed the check (a connector type that joins, or one undefined that Revit decides),
                        // yet Revit may take none of them: skip them all, roll the group back and succeed without a circuit.
                        CommandResult RefusedAll(string why)
                        {
                            if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                            if (group.HasStarted() && !group.HasEnded()) group.RollBack();
                            foreach (var id in ids)
                                skipped.Add(new JObject { ["element_id"] = id, ["reason"] = "Revit refused it for a " + systemType + " circuit: " + why });
                            return NoCircuit(systemType, skipped, "Revit refused every remaining element for a " + systemType
                                + " circuit (" + why + "); nothing was created.");
                        }

                        try
                        {
                            circuit = ElectricalSystem.Create(doc, ids.Select(RevitCompat.ToElementId).ToList(), systemType);
                        }
                        catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                        {
                            return RefusedAll(ex.Message);
                        }
                        if (circuit == null) throw new InvalidOperationException("Revit returned no circuit.");
                        doc.Regenerate();
                        members = circuit.Elements.Cast<Element>().Select(e => RevitCompat.GetId(e.Id)).ToList();
                        if (members.Count == 0) return RefusedAll("Revit made an empty circuit");
                        foreach (var id in ids.Where(id => !members.Contains(id)))
                            skipped.Add(new JObject { ["element_id"] = id, ["reason"] = "Revit left it out of the circuit" });
                        if (tx.Commit() != TransactionStatus.Committed)
                        {
                            group.RollBack();
                            return CommandResult.Fail("NOT_COMMITTED: Revit rolled the circuit back; see failures. Nothing was created.",
                                new JObject { ["skipped"] = skipped });
                        }
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                        if (group.HasStarted() && !group.HasEnded()) group.RollBack();
                        return CommandResult.Fail("CREATE_FAILED: could not create the circuit: " + ex.Message + " Nothing was created.",
                            new JObject { ["skipped"] = skipped });
                    }
                }

                string panelError = null;
                if (panel != null)
                {
                    using (var tx = new Transaction(doc, "Plinth: Assign circuit to panel"))
                    {
                        tx.Start();
                        try
                        {
                            circuit.SelectPanel(panel);
                            doc.Regenerate();
                            if (circuit.BaseEquipment == null || circuit.BaseEquipment.Id != panel.Id)
                                throw new InvalidOperationException("Revit did not assign the circuit to the panel.");
                            if (tx.Commit() != TransactionStatus.Committed)
                                panelError = "Revit raised a warning or error and undid the assignment (see failures).";
                        }
                        catch (Exception ex)
                        {
                            if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                            panelError = ex.Message;
                        }
                    }
                    if (panelError != null)
                        panelError = "Revit refused panel " + RevitCompat.GetId(panel.Id) + " (" + panel.Name + "): " + panelError
                            + " The circuit was kept without a panel.";
                }
                group.Assimilate();

                return CommandResult.Ok(new JObject
                {
                    ["circuit_id"] = RevitCompat.GetId(circuit.Id),
                    ["circuit_number"] = circuit.CircuitNumber,
                    ["load_name"] = circuit.LoadName,
                    ["system_type"] = systemType.ToString(),
                    ["panel_id"] = circuit.BaseEquipment == null ? null : (JToken)RevitCompat.GetId(circuit.BaseEquipment.Id),
                    ["panel_name"] = circuit.PanelName,
                    ["element_ids"] = new JArray(members),
                    ["created_ids"] = new JArray(RevitCompat.GetId(circuit.Id)),
                    ["skipped"] = skipped,
                    ["panel_error"] = panelError
                });
            }
        }

        /// <summary>best_effort's success without a circuit: nothing was created and nothing is left in the document.</summary>
        private static CommandResult NoCircuit(ElectricalSystemType systemType, JArray skipped, string note) =>
            CommandResult.Ok(new JObject
            {
                ["circuit_id"] = null,
                ["circuit_number"] = null,
                ["load_name"] = null,
                ["system_type"] = systemType.ToString(),
                ["panel_id"] = null,
                ["panel_name"] = null,
                ["element_ids"] = new JArray(),
                ["created_ids"] = new JArray(),
                ["skipped"] = skipped,
                ["panel_error"] = null,
                ["note"] = note
            });

        /// <summary>
        /// Why the element cannot join a circuit of this type, or null when it has a free connector that can (CircuitTypes.Fit:
        /// one of a type that joins it, or one whose type is undefined or unreadable, which Revit then decides).
        /// </summary>
        private static string Check(Document doc, long id, ElectricalSystemType systemType)
        {
            var element = doc.GetElement(RevitCompat.ToElementId(id));
            if (element == null) return "not in this document";
            var manager = MepHelpers.GetConnectorManager(element);
            if (!(element is FamilyInstance) || manager == null) return "a " + (element.Category?.Name ?? element.GetType().Name) + " with no electrical connector";
            var electrical = manager.Connectors.Cast<Connector>().Where(c => c.Domain == Domain.DomainElectrical).ToList();
            if (electrical.Count == 0) return "its family (" + MepHelpers.TypeName(doc, element) + ") has no electrical connector";
            var ofType = electrical.Where(c => CircuitTypes.Fit(systemType.ToString(), SafeType(c)?.ToString()) != CircuitTypes.Refused).ToList();
            if (ofType.Count == 0)
                return "no connector that can join a " + systemType + " circuit (it has "
                    + string.Join(", ", electrical.Select(c => SafeType(c)?.ToString() ?? "unknown").Distinct()) + ")";
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
