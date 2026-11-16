// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Parameters/SetParameterValueByGuidHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: values use the shared rules (lengths in mm through Mm; m2, m3 and degrees through Revit's unit
// conversion; rvt-mcp converted "m2" and "m3" with mm-squared/cubed factors, a factor of 10^6 / 10^9 off); value may
// be a JSON number, boolean or string; valueType "display" still uses Revit's display-string parser; per-element
// failures with allOrNothing: false are reported as failedCount so batch_execute sees them; a type shared by
// several elements is set once and the others are listed; old and new values are read back in wire units.
// Duplicate before edit (TypeEditGuard): a type owner (target type, or auto falling back to the type) and an id that is
// itself a type or material are changed only with duplicate_as (the copy gets the value and the elementIds of that
// type move onto it) or confirm_edit_all (= the exact instance count); otherwise TYPE_IN_USE before anything changes.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Parameters
{
    public sealed class SetParameterValueByGuidHandler : IRevitCommand
    {
        private const int MaxIds = 5000;

        public string Name => "set_parameter_value_by_guid";
        public string Description =>
            "Set a shared parameter (by GUID) on elements, or on their types (target type, or auto: instance first, then type). "
            + "value: text, number, true/false for Yes/No, or an element id. Numbers are read in unit (auto = mm / m2 / m3 / deg from the "
            + "parameter's data type; internal = Revit's own units). valueType display parses Revit's formatted text. "
            + "allOrNothing (default true) rolls everything back on the first failure." + TypeEditGuard.Rule
            + " Several types in one call: duplicate_as {\"<typeId>\": \"name\"} and confirm_edit_all {\"<typeId>\": N}.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""guid"":{""type"":""string""},
            ""value"":{},
            ""valueType"":{""type"":""string"",""enum"":[""auto"",""display""]},
            ""unit"":{""type"":""string"",""enum"":[""auto"",""internal"",""mm"",""m2"",""m3"",""deg""]},
            ""target"":{""type"":""string"",""enum"":[""auto"",""instance"",""type""]},
            ""allOrNothing"":{""type"":""boolean""},
            " + TypeEditGuard.SchemaFields + @"},""required"":[""elementIds"",""guid"",""value""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!ParametersHelpers.TryGuid(p.Value<string>("guid"), out var guid, out var failure)) return failure;
            var ids = p["elementIds"].ToObject<long[]>().Distinct().ToArray();
            if (ids.Length == 0 || ids.Length > MaxIds) return CommandResult.Fail("INVALID_PARAMS: elementIds must hold 1 to " + MaxIds + " ids.");
            var value = p["value"];
            var display = (p.Value<string>("valueType") ?? "auto") == "display";
            if (display && value.Type != JTokenType.String) return CommandResult.Fail("INVALID_PARAMS: valueType display needs value as a string.");
            var unit = p.Value<string>("unit") ?? "auto";
            var target = p.Value<string>("target") ?? "auto";
            var allOrNothing = p.Value<bool?>("allOrNothing") ?? true;

            var failed = new JArray();
            var warnings = new JArray();
            var sharedTypeSkips = new JArray();
            var work = new List<(long id, Element owner, string used)>();
            var seenTypes = new HashSet<long>();
            foreach (var id in ids)
            {
                var element = doc.GetElement(RevitCompat.ToElementId(id));
                if (element == null)
                {
                    failed.Add(new JObject { ["elementId"] = id, ["error"] = "element not found" });
                    continue;
                }
                var type = doc.GetElement(element.GetTypeId());
                Element owner = null;
                var used = "instance";
                if (target != "type" && element.get_Parameter(guid) != null)
                {
                    owner = element;
                    if (target == "auto" && type?.get_Parameter(guid) != null)
                        warnings.Add("element " + id + " has the parameter on both instance and type; the instance was used.");
                }
                else if (target != "instance" && type?.get_Parameter(guid) != null)
                {
                    owner = type;
                    used = "type";
                }
                if (owner == null)
                {
                    failed.Add(new JObject { ["elementId"] = id, ["error"] = "parameter " + guid + " is not on this element" + (target == "instance" ? "" : " or its type") });
                    continue;
                }
                if (used == "type" && !seenTypes.Add(RevitCompat.GetId(owner.Id)))
                {
                    sharedTypeSkips.Add(new JObject { ["elementId"] = id, ["typeId"] = RevitCompat.GetId(owner.Id) });
                    continue;
                }
                work.Add((id, owner, used));
            }
            if (allOrNothing && failed.Count > 0)
                return CommandResult.Fail("SET_FAILED: element " + failed[0].Value<long>("elementId") + ": " + failed[0].Value<string>("error") + ". Nothing changed.",
                    new JObject { ["failed"] = failed });

            // The elementIds whose (type) owner is each shared element: on a copy, exactly these move onto it.
            var named = new Dictionary<long, List<long>>();
            foreach (var w in work)
                if (w.used == "type") named[RevitCompat.GetId(w.owner.Id)] = new List<long> { w.id };
            foreach (var skip in sharedTypeSkips) named[skip.Value<long>("typeId")].Add(skip.Value<long>("elementId"));
            var edits = work.Where(w => TypeEditGuard.IsShared(w.owner))
                .Select(w => new SharedEdit(w.owner, named.TryGetValue(RevitCompat.GetId(w.owner.Id), out var ids2) ? ids2 : null));
            if (!TypeEditGuard.TryPlan(doc, p, edits, out var plan, out failure)) return failure;

            var updated = new JArray();
            using (var tx = new Transaction(doc, "B2M: Set parameter by GUID"))
            {
                tx.Start();
                try
                {
                    plan.Apply(doc);
                    foreach (var w in work)
                    {
                        var owner = plan.TargetFor(w.owner);
                        var parameter = owner.get_Parameter(guid);
                        var before = ParametersHelpers.Read(doc, parameter);
                        string error = null, unitUsed = null;
                        bool ok;
                        using (var sub = new SubTransaction(doc))
                        {
                            sub.Start();
                            if (display)
                            {
                                ok = !parameter.IsReadOnly && parameter.SetValueString(value.Value<string>());
                                if (!ok) error = parameter.IsReadOnly ? "parameter is read-only" : "Revit could not parse '" + value + "'";
                            }
                            else ok = ParametersHelpers.TrySet(doc, parameter, value, unit, out unitUsed, out error);
                            if (ok) sub.Commit();
                            else sub.RollBack();
                        }
                        if (!ok)
                        {
                            // A copy made for this value must not be left behind without it (with elements moved onto it).
                            if (allOrNothing || owner.Id != w.owner.Id)
                            {
                                tx.RollBack();
                                return CommandResult.Fail("SET_FAILED: element " + w.id + ": " + error + ". Nothing changed.");
                            }
                            failed.Add(new JObject { ["elementId"] = w.id, ["error"] = error });
                            continue;
                        }
                        updated.Add(new JObject
                        {
                            ["elementId"] = w.id,
                            ["targetUsed"] = w.used,
                            ["targetElementId"] = RevitCompat.GetId(owner.Id),
                            ["parameterName"] = parameter.Definition.Name,
                            ["unitUsed"] = unitUsed,
                            ["old"] = before,
                            ["new"] = ParametersHelpers.Read(doc, parameter)
                        });
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures. Nothing changed.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("SET_FAILED: " + ex.Message + " Nothing changed.");
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["guid"] = guid.ToString("d"),
                ["requested"] = ids.Length,
                ["updatedCount"] = updated.Count,
                ["failedCount"] = failed.Count,
                ["allOrNothing"] = allOrNothing,
                ["updated"] = updated,
                ["failed"] = failed,
                ["sharedTypeSkipped"] = sharedTypeSkips,
                ["shared_edits"] = plan.Report(),
                ["warnings"] = warnings
            });
        }
    }
}
