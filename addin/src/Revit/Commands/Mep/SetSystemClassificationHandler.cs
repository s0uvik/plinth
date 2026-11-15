// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Mep/SetSystemClassificationHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: system_id is required and the command always writes, so ReadOnly is false; rvt-mcp's read-only
// report mode (no system_id) is dropped because one command cannot be both, and get_mep_element_connectors now
// reports each connector's system instead; element_ids is capped at 500; when nothing is added the transaction is
// rolled back (and it is a failure when every element failed); the per-element results are kept; IDs are numbers.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Mep
{
    public sealed class SetSystemClassificationHandler : IRevitCommand
    {
        private const int MaxElements = 500;

        public string Name => "set_system_classification";
        public string Description =>
            "Add MEP elements (element_ids, up to 500) to an existing mechanical (duct) or piping system (system_id). Each element needs "
            + "an End connector of the system's domain. Returns added, already_member and failed (with reasons); the elements that "
            + "could be added are kept. Use get_mep_element_connectors to see an element's current system.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""system_id"":{""type"":""integer""}},
            ""required"":[""element_ids"",""system_id""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var ids = p["element_ids"].Values<long>().Distinct().ToList();
            if (ids.Count == 0) return CommandResult.Fail("INVALID_PARAMS: element_ids must not be empty.");
            if (ids.Count > MaxElements) return CommandResult.Fail("INVALID_PARAMS: at most " + MaxElements + " element_ids per call.");

            var systemId = p.Value<long>("system_id");
            var systemElement = doc.GetElement(RevitCompat.ToElementId(systemId));
            if (systemElement == null) return CommandResult.Fail("ELEMENT_NOT_FOUND: no element has id " + systemId + ".");
            var mechanical = systemElement as MechanicalSystem;
            var piping = systemElement as PipingSystem;
            if (mechanical == null && piping == null)
                return CommandResult.Fail("NOT_A_DUCT_OR_PIPING_SYSTEM: element " + systemId + " is a " + systemElement.GetType().Name
                    + "; only mechanical and piping systems take new members here.");
            var domain = mechanical != null ? Domain.DomainHvac : Domain.DomainPiping;

            HashSet<long> members;
            try
            {
                members = new HashSet<long>(MepSystemMembership.Read((MEPSystem)systemElement).AllElements.Select(e => RevitCompat.GetId(e.Id)));
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("READ_FAILED: " + ex.Message);
            }

            var added = new List<long>();
            var alreadyMember = new List<long>();
            var failed = new List<object>();
            using (var tx = new Transaction(doc, "B2M: Set System Classification"))
            {
                tx.Start();
                try
                {
                    foreach (var id in ids)
                    {
                        if (members.Contains(id))
                        {
                            alreadyMember.Add(id);
                            continue;
                        }
                        var element = doc.GetElement(RevitCompat.ToElementId(id));
                        if (element == null)
                        {
                            failed.Add(new { element_id = id, error = "ELEMENT_NOT_FOUND" });
                            continue;
                        }
                        var manager = MepHelpers.GetConnectorManager(element);
                        if (manager == null)
                        {
                            failed.Add(new { element_id = id, error = "NO_CONNECTORS: the element has no MEP connectors." });
                            continue;
                        }
                        if (!manager.Connectors.Cast<Connector>().Any(c => c.ConnectorType == ConnectorType.End && c.Domain == domain))
                        {
                            failed.Add(new { element_id = id, error = "NO_MATCHING_CONNECTOR: no End connector in the " + MepHelpers.DomainName(domain) + " domain." });
                            continue;
                        }
                        try
                        {
                            if (mechanical != null) mechanical.Add(manager.Connectors);
                            else piping.Add(manager.Connectors);
                            added.Add(id);
                            members.Add(id);
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new { element_id = id, error = "REJECTED: " + ex.Message });
                        }
                    }

                    var result = new
                    {
                        system_id = systemId,
                        system_name = MepHelpers.SafeName(systemElement),
                        added,
                        already_member = alreadyMember,
                        failed
                    };
                    if (added.Count == 0)
                    {
                        tx.RollBack();
                        return alreadyMember.Count > 0 && failed.Count == 0
                            ? CommandResult.Ok(result)
                            : CommandResult.Fail("ADD_FAILED: no element could be added; see failed. Nothing changed.", result);
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                    return CommandResult.Ok(result);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CLASSIFY_FAILED: " + ex.Message + " Nothing changed.");
                }
            }
        }
    }
}
