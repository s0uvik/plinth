// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Parameters/BindSharedParameterHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: categories must allow bound parameters and a miss lists the valid ones; a parameter group
// miss lists the built-in groups; a request already covered by the existing binding is a no-op instead of
// needing allowRebind; when the GUID is already in the project the file is not needed; the user's configured
// shared parameter file is restored after a custom path; the transaction is checked for Committed.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Parameters
{
    public sealed class BindSharedParameterHandler : IRevitCommand
    {
        public string Name => "bind_shared_parameter";
        public string Description =>
            "Bind a shared parameter (by GUID) to categories (OST_Doors or display names) as an instance or type parameter, under "
            + "parameterGroupId (Forge group id, default Data). Changing an existing binding's categories or kind needs allowRebind: true "
            + "(categories are merged).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""guid"":{""type"":""string""},
            ""categories"":{""type"":""array"",""items"":{""type"":""string""}},
            ""bindingKind"":{""type"":""string"",""enum"":[""instance"",""type""]},
            ""parameterGroupId"":{""type"":""string""},
            ""sharedParameterFilePath"":{""type"":""string""},
            ""allowRebind"":{""type"":""boolean""}},""required"":[""guid"",""categories""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!ParametersHelpers.RequireProject(doc, out var failure)) return failure;
            if (!ParametersHelpers.TryGuid(p.Value<string>("guid"), out var guid, out failure)) return failure;
            if (!ParametersHelpers.TryGroup(p.Value<string>("parameterGroupId"), out var groupId, out failure)) return failure;
            var kind = p.Value<string>("bindingKind") ?? "instance";
            var allowRebind = p.Value<bool?>("allowRebind") ?? false;
            var names = p["categories"].ToObject<string[]>();
            if (names.Length == 0) return CommandResult.Fail("INVALID_PARAMS: categories must not be empty.");
            var categories = new List<Category>();
            foreach (var n in names)
            {
                if (!ParametersHelpers.TryBindableCategory(doc, n, out var c, out failure)) return failure;
                if (categories.All(x => x.Id != c.Id)) categories.Add(c);
            }

            var existing = ParametersHelpers.Bindings(doc).FirstOrDefault(kv => ParametersHelpers.SharedGuidOf(doc, kv.Key) == guid);
            var existingIds = new HashSet<long>();
            if (existing.Value?.Categories != null)
                foreach (Category c in existing.Value.Categories) existingIds.Add(RevitCompat.GetId(c.Id));

            var result = new JObject { ["guid"] = guid.ToString("d"), ["bindingKind"] = kind, ["parameterGroupId"] = groupId.TypeId };
            if (existing.Key != null)
            {
                var sameKind = ParametersHelpers.KindOf(existing.Value) == kind;
                if (sameKind && categories.All(c => existingIds.Contains(RevitCompat.GetId(c.Id))))
                {
                    result["bound"] = true;
                    result["alreadyBound"] = true;
                    result["name"] = existing.Key.Name;
                    result["categories"] = ParametersHelpers.CategoriesJson(existing.Value);
                    return CommandResult.Ok(result);
                }
                if (!allowRebind)
                    return CommandResult.Fail("ALREADY_BOUND: '" + existing.Key.Name + "' is bound as " + ParametersHelpers.KindOf(existing.Value)
                        + " to other categories; pass allowRebind: true to merge categories" + (sameKind ? "." : " and change the kind (values may be lost)."),
                        new JObject { ["bindingKind"] = ParametersHelpers.KindOf(existing.Value), ["categories"] = ParametersHelpers.CategoriesJson(existing.Value) });
            }

            ExternalDefinition external = null;
            ParametersHelpers.SharedParameterFile file = null;
            if (existing.Key == null)
            {
                if (!ParametersHelpers.SharedParameterFile.TryOpen(app.Application, p.Value<string>("sharedParameterFilePath"), false,
                        out file, out _, out failure)) return failure;
                external = file.Find(guid, out _);
                if (external == null)
                {
                    var path = file.Path;
                    file.Dispose();
                    return CommandResult.Fail("PARAMETER_NOT_FOUND: GUID " + guid + " is not in the shared parameter file " + path
                        + " (see list_shared_parameters, or create_shared_parameter).");
                }
            }

            using (file)
            using (var tx = new Transaction(doc, "Plinth: Bind shared parameter"))
            {
                tx.Start();
                try
                {
                    var set = app.Application.Create.NewCategorySet();
                    foreach (var c in categories) set.Insert(c);
                    if (existing.Key != null)
                        foreach (var id in existingIds)
                        {
                            var c = Category.GetCategory(doc, RevitCompat.ToElementId(id));
                            if (c != null && !set.Contains(c)) set.Insert(c);
                        }
                    Binding binding = kind == "type"
                        ? (Binding)app.Application.Create.NewTypeBinding(set)
                        : app.Application.Create.NewInstanceBinding(set);
                    var ok = existing.Key != null
                        ? doc.ParameterBindings.ReInsert(existing.Key, binding, groupId)
                        : doc.ParameterBindings.Insert(external, binding, groupId);
                    if (!ok)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("BIND_FAILED: Revit refused the binding (a project parameter with the same name may exist).");
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("BIND_FAILED: " + ex.Message);
                }
            }

            var now = ParametersHelpers.Bindings(doc).FirstOrDefault(kv => ParametersHelpers.SharedGuidOf(doc, kv.Key) == guid);
            result["bound"] = true;
            result["alreadyBound"] = false;
            result["rebuiltBinding"] = existing.Key != null;
            result["name"] = now.Key?.Name ?? external?.Name;
            result["categories"] = ParametersHelpers.CategoriesJson(now.Value);
            if (existing.Key != null && ParametersHelpers.KindOf(existing.Value) != kind)
                result["warning"] = "The binding kind changed from " + ParametersHelpers.KindOf(existing.Value) + " to " + kind + "; existing values may be lost.";
            return CommandResult.Ok(result);
        }
    }
}
