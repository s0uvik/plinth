// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Parameters/RemoveParameterBindingHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: misses list the project's bindings or the parameter's bound categories; a name match is exact
// before case-insensitive; the original parameter group is kept when re-inserting (rvt-mcp fell back to Data on
// error); the transaction is checked for Committed. dryRun still defaults to true, as in rvt-mcp.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Parameters
{
    public sealed class RemoveParameterBindingHandler : IRevitCommand
    {
        public string Name => "remove_parameter_binding";
        public string Description =>
            "Remove a project/shared parameter binding (by name or guid) entirely with removeAllCategories: true, or unbind some categories. "
            + "Removing the whole binding deletes the parameter's values. dryRun defaults to TRUE: pass dryRun: false to change the model.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""name"":{""type"":""string""},
            ""guid"":{""type"":""string""},
            ""categories"":{""type"":""array"",""items"":{""type"":""string""}},
            ""removeAllCategories"":{""type"":""boolean""},
            ""dryRun"":{""type"":""boolean""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!ParametersHelpers.RequireProject(doc, out var failure)) return failure;
            var name = p.Value<string>("name");
            var guidText = p.Value<string>("guid");
            var removeAll = p.Value<bool?>("removeAllCategories") ?? false;
            var dryRun = p.Value<bool?>("dryRun") ?? true;
            var names = p["categories"]?.ToObject<string[]>() ?? new string[0];
            if (string.IsNullOrEmpty(name) == string.IsNullOrEmpty(guidText))
                return CommandResult.Fail("INVALID_PARAMS: pass name or guid (one of them).");
            if (removeAll && names.Length > 0) return CommandResult.Fail("INVALID_PARAMS: pass categories or removeAllCategories: true, not both.");
            if (!removeAll && names.Length == 0) return CommandResult.Fail("INVALID_PARAMS: pass categories to unbind, or removeAllCategories: true.");

            var all = ParametersHelpers.Bindings(doc);
            List<KeyValuePair<Definition, ElementBinding>> matches;
            if (!string.IsNullOrEmpty(guidText))
            {
                if (!ParametersHelpers.TryGuid(guidText, out var guid, out failure)) return failure;
                matches = all.Where(kv => ParametersHelpers.SharedGuidOf(doc, kv.Key) == guid).ToList();
            }
            else
            {
                matches = all.Where(kv => string.Equals(kv.Key.Name, name, StringComparison.Ordinal)).ToList();
                if (matches.Count == 0) matches = all.Where(kv => string.Equals(kv.Key.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (matches.Count != 1)
                return CommandResult.Fail(matches.Count == 0
                        ? "BINDING_NOT_FOUND: no parameter binding " + (string.IsNullOrEmpty(guidText) ? "named '" + name + "'" : "with GUID " + guidText) + "."
                        : "BINDING_AMBIGUOUS: several bindings are named '" + name + "'; pass guid.",
                    new JObject
                    {
                        ["valid_bindings"] = new JArray((matches.Count == 0 ? all : matches).OrderBy(kv => kv.Key.Name).Take(200).Select(kv => new JObject
                        {
                            ["name"] = kv.Key.Name,
                            ["guid"] = ParametersHelpers.SharedGuidOf(doc, kv.Key)?.ToString("d")
                        }))
                    });
            var target = matches[0];

            var current = new List<Category>();
            foreach (Category c in target.Value.Categories) if (c != null) current.Add(c);
            var removed = new List<Category>();
            var remaining = new List<Category>(current);
            if (removeAll)
            {
                removed.AddRange(current);
                remaining.Clear();
            }
            else
            {
                foreach (var n in names)
                {
                    if (!ParametersHelpers.TryBindableCategory(doc, n, out var c, out failure)) return failure;
                    var hit = remaining.FirstOrDefault(x => x.Id == c.Id);
                    if (hit == null)
                    {
                        if (removed.Any(x => x.Id == c.Id)) continue;
                        return CommandResult.Fail("CATEGORY_NOT_BOUND: '" + target.Key.Name + "' is not bound to " + c.Name + ".",
                            new JObject { ["bound_categories"] = ParametersHelpers.CategoriesJson(current) });
                    }
                    remaining.Remove(hit);
                    removed.Add(hit);
                }
                if (remaining.Count == 0)
                    return CommandResult.Fail("INVALID_PARAMS: that would unbind every category; pass removeAllCategories: true to remove the binding.");
            }

            var result = new JObject
            {
                ["dryRun"] = dryRun,
                ["name"] = target.Key.Name,
                ["guid"] = ParametersHelpers.SharedGuidOf(doc, target.Key)?.ToString("d"),
                ["bindingKind"] = ParametersHelpers.KindOf(target.Value),
                ["removesBinding"] = removeAll,
                ["removedCategories"] = ParametersHelpers.CategoriesJson(removed),
                ["remainingCategories"] = ParametersHelpers.CategoriesJson(remaining)
            };
            if (dryRun)
            {
                result["changed"] = false;
                return CommandResult.Ok(result);
            }

            using (var tx = new Transaction(doc, "B2M: Remove parameter binding"))
            {
                tx.Start();
                try
                {
                    bool ok;
                    if (removeAll) ok = doc.ParameterBindings.Remove(target.Key);
                    else
                    {
                        var set = app.Application.Create.NewCategorySet();
                        foreach (var c in remaining) set.Insert(c);
                        Binding binding = target.Value is TypeBinding
                            ? (Binding)app.Application.Create.NewTypeBinding(set)
                            : app.Application.Create.NewInstanceBinding(set);
                        ok = doc.ParameterBindings.ReInsert(target.Key, binding, target.Key.GetGroupTypeId());
                    }
                    if (!ok)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("UNBIND_FAILED: Revit refused to change the binding.");
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("UNBIND_FAILED: " + ex.Message);
                }
            }
            result["changed"] = true;
            return CommandResult.Ok(result);
        }
    }
}
