// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Sheets/SetTitleblockParametersHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: sheet and title block resolution shared with get_titleblock_parameters (a miss lists the
// valid sheets; several title blocks need title_block_id); a parameter that is only read-only where it was
// found is an error and nothing changes (rvt-mcp skipped it and could report success having set nothing);
// a missing parameter lists the writable ones; values must have the parameter's JSON type (rvt-mcp parsed
// numbers out of text); units through SheetsHelpers/Mm; the camelCase aliases are gone.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Sheets
{
    public sealed class SetTitleblockParametersHandler : IRevitCommand
    {
        public string Name => "set_titleblock_parameters";
        public string Description =>
            "Set title block parameters on a sheet (sheet_id or sheet_number, default the active sheet): parameters is {\"name\": value}. "
            + "target instance (default), type, or both; a type change affects every sheet using that title block type. "
            + "Lengths are mm, areas m², volumes m³, angles degrees; yes/no accepts true/false. All or nothing.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""sheet_id"":{""type"":""integer""},
            ""sheet_number"":{""type"":""string""},
            ""title_block_id"":{""type"":""integer""},
            ""target"":{""type"":""string"",""enum"":[""instance"",""type"",""both""]},
            ""parameters"":{""type"":""object""}},
            ""required"":[""parameters""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var target = p.Value<string>("target") ?? "instance";
            var values = (JObject)p["parameters"];
            if (!values.Properties().Any()) return CommandResult.Fail("INVALID_PARAMS: parameters cannot be empty.");
            if (!GetTitleblockParametersHandler.TryResolve(doc, p, out var sheet, out var titleBlock, out var failure)) return failure;
            var type = doc.GetElement(titleBlock.GetTypeId()) as FamilySymbol;

            var scopes = new List<KeyValuePair<string, Element>>();
            if (target != "type") scopes.Add(new KeyValuePair<string, Element>("instance", titleBlock));
            if (target != "instance" && type != null) scopes.Add(new KeyValuePair<string, Element>("type", type));

            // Resolve every parameter before the transaction so a miss changes nothing.
            var plan = new List<Tuple<string, string, Parameter, JToken>>();
            var results = new JObject();
            foreach (var property in values.Properties())
            {
                var found = scopes.Select(s => new { Scope = s.Key, Param = SheetsHelpers.FindParameter(s.Value, property.Name) })
                    .Where(x => x.Param != null).ToList();
                if (found.Count == 0)
                    return CommandResult.Fail("PARAMETER_NOT_FOUND: '" + property.Name + "' is not a " + target + " parameter of the title block.",
                        new JObject { ["writable_parameters"] = WritableNames(scopes) });
                var writable = found.Where(x => !x.Param.IsReadOnly).ToList();
                if (writable.Count == 0)
                    return CommandResult.Fail("PARAMETER_READ_ONLY: '" + property.Name + "' is read-only on the title block " + string.Join(" and ", found.Select(x => x.Scope)) + ". Nothing was changed.",
                        new JObject { ["writable_parameters"] = WritableNames(scopes) });
                var targets = new JObject();
                foreach (var x in found)
                {
                    if (x.Param.IsReadOnly) targets[x.Scope] = new JObject { ["status"] = "skipped", ["reason"] = "read-only" };
                    else
                    {
                        plan.Add(Tuple.Create(property.Name, x.Scope, x.Param, property.Value));
                        targets[x.Scope] = new JObject { ["status"] = "set", ["storage_type"] = x.Param.StorageType.ToString() };
                    }
                }
                results[property.Name] = new JObject { ["status"] = writable.Count == found.Count ? "set" : "partial", ["targets"] = targets };
            }

            using (var tx = new Transaction(doc, "B2M: Set Title Block Parameters"))
            {
                tx.Start();
                try
                {
                    foreach (var step in plan)
                    {
                        if (!SheetsHelpers.TrySet(step.Item3, step.Item4, out var error))
                        {
                            tx.RollBack();
                            return CommandResult.Fail("SET_FAILED: " + step.Item2 + " parameter '" + step.Item1 + "': " + error + " Nothing was changed.");
                        }
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("SET_FAILED: could not set the title block parameters: " + ex.Message + " Nothing was changed.");
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["sheet_id"] = RevitCompat.GetId(sheet.Id),
                ["sheet_number"] = sheet.SheetNumber,
                ["title_block_instance_id"] = RevitCompat.GetId(titleBlock.Id),
                ["title_block_type_id"] = RevitCompat.GetIdOrNull(type?.Id),
                ["parameters"] = results
            });
        }

        private static JArray WritableNames(IEnumerable<KeyValuePair<string, Element>> scopes) =>
            new JArray(scopes.SelectMany(s => s.Value.Parameters.Cast<Parameter>()
                    .Where(x => x.Definition != null && !x.IsReadOnly).Select(x => x.Definition.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(200));
    }
}
