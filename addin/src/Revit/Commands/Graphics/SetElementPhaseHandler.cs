// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Organization/SetElementPhaseHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: moved to the graphics toolset next to the other phase tools; all or nothing (rvt-mcp ran
// one sub-transaction per element and committed what worked): missing elements and elements without an
// editable phase parameter fail before the transaction, and any Revit refusal or warning (for example
// demolished before created) rolls back the whole call through FailureScope; at most 5000 ids; id and name for
// the same field together are an error; a missing phase lists the valid ones.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Graphics
{
    public sealed class SetElementPhaseHandler : IRevitCommand
    {
        public string Name => "set_element_phase";
        public string Description => "Set Phase Created and/or Phase Demolished of elements, each by phase id or name. "
            + "phase_demolished_id -1 or phase_demolished_name 'None' clears demolition. All elements change or none. At most 5000 ids.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""phase_created_id"":{""type"":""integer""},
            ""phase_created_name"":{""type"":""string""},
            ""phase_demolished_id"":{""type"":""integer""},
            ""phase_demolished_name"":{""type"":""string""}},""required"":[""element_ids""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            if (!GraphicsHelpers.TryIds(p, "element_ids", true, out var ids, out var failure)) return failure;

            var createdId = p.Value<long?>("phase_created_id");
            var createdName = p.Value<string>("phase_created_name");
            var demolishedId = p.Value<long?>("phase_demolished_id");
            var demolishedName = p.Value<string>("phase_demolished_name");
            if (createdId.HasValue && createdName != null) return CommandResult.Fail("INVALID_PARAMS: pass phase_created_id or phase_created_name, not both.");
            if (demolishedId.HasValue && demolishedName != null) return CommandResult.Fail("INVALID_PARAMS: pass phase_demolished_id or phase_demolished_name, not both.");
            var setCreated = createdId.HasValue || createdName != null;
            var setDemolished = demolishedId.HasValue || demolishedName != null;
            if (!setCreated && !setDemolished)
                return CommandResult.Fail("INVALID_PARAMS: give phase_created_id or phase_created_name, and/or phase_demolished_id or phase_demolished_name.");

            Phase created = null;
            if (setCreated && !GraphicsHelpers.TryPhase(doc, createdId, createdName, "phase_created", out created, out failure)) return failure;

            Phase demolished = null;
            var clearDemolished = setDemolished && (demolishedId == -1 || string.Equals(demolishedName?.Trim(), "None", StringComparison.OrdinalIgnoreCase));
            if (setDemolished && !clearDemolished
                && !GraphicsHelpers.TryPhase(doc, demolishedId, demolishedName, "phase_demolished", out demolished, out failure)) return failure;
            var demolishedValue = clearDemolished ? ElementId.InvalidElementId : demolished?.Id;

            var elements = new List<Element>();
            var missing = new List<long>();
            var refused = new List<JObject>();
            foreach (var id in ids)
            {
                var element = doc.GetElement(RevitCompat.ToElementId(id));
                if (element == null)
                {
                    missing.Add(id);
                    continue;
                }
                var problem = setCreated ? Check(element, BuiltInParameter.PHASE_CREATED, "Phase Created") : null;
                problem = problem ?? (setDemolished ? Check(element, BuiltInParameter.PHASE_DEMOLISHED, "Phase Demolished") : null);
                if (problem != null) refused.Add(new JObject { ["element_id"] = id, ["error"] = problem });
                else elements.Add(element);
            }
            if (missing.Count > 0)
                return CommandResult.Fail("ELEMENT_NOT_FOUND: " + missing.Count + " id(s) are not elements in this document; nothing changed.",
                    new JObject { ["missing_ids"] = new JArray(missing.Take(GraphicsHelpers.MaxReported)) });
            if (refused.Count > 0)
                return CommandResult.Fail("PHASE_NOT_EDITABLE: " + refused.Count + " element(s) have no editable phase parameter; nothing changed.",
                    new JObject { ["elements"] = new JArray(refused.Take(GraphicsHelpers.MaxReported)) });

            using (var tx = new Transaction(doc, "B2M: Set Element Phase"))
            {
                tx.Start();
                var current = ElementId.InvalidElementId;
                try
                {
                    foreach (var element in elements)
                    {
                        current = element.Id;
                        if (setCreated && !element.get_Parameter(BuiltInParameter.PHASE_CREATED).Set(created.Id))
                            throw new InvalidOperationException("Revit refused Phase Created '" + created.Name + "'.");
                        if (setDemolished && !element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED).Set(demolishedValue))
                            throw new InvalidOperationException("Revit refused the Phase Demolished value.");
                    }
                    if (tx.Commit() != TransactionStatus.Committed) return GraphicsHelpers.NotCommitted();
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("SET_FAILED: element " + RevitCompat.GetId(current) + ": " + ex.Message + " Nothing changed.",
                        new JObject { ["element_id"] = RevitCompat.GetId(current) });
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["element_count"] = elements.Count,
                ["phase_created"] = setCreated ? new JObject { ["id"] = RevitCompat.GetId(created.Id), ["name"] = created.Name } : null,
                ["phase_demolished"] = !setDemolished ? null
                    : clearDemolished ? new JObject { ["id"] = -1, ["name"] = "None" }
                    : new JObject { ["id"] = RevitCompat.GetId(demolished.Id), ["name"] = demolished.Name }
            });
        }

        private static string Check(Element element, BuiltInParameter bip, string label)
        {
            var parameter = element.get_Parameter(bip);
            if (parameter == null) return label + " is not present on this element.";
            if (parameter.IsReadOnly) return label + " is read-only on this element.";
            return null;
        }
    }
}
