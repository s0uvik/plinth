// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Materials/SetMaterialIdentityHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: all or nothing (rvt-mcp committed when at least one field was set and reported the rest);
// fields are found by built-in parameter only (URL via ALL_MODEL_URL, not a name lookup that fails in other
// languages); cost is a number; description is added; SetValueString guessing is dropped.
// Duplicate before edit (TypeEditGuard): a material is shared, so it is changed only with duplicate_as (a copy named
// so gets the change; nothing else moves onto it) or confirm_edit_all (= its best-effort user count); otherwise
// MATERIAL_IN_USE before anything changes.

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Materials
{
    public sealed class SetMaterialIdentityHandler : IRevitCommand
    {
        private static readonly KeyValuePair<string, BuiltInParameter>[] Fields =
        {
            new KeyValuePair<string, BuiltInParameter>("manufacturer", BuiltInParameter.ALL_MODEL_MANUFACTURER),
            new KeyValuePair<string, BuiltInParameter>("model", BuiltInParameter.ALL_MODEL_MODEL),
            new KeyValuePair<string, BuiltInParameter>("cost", BuiltInParameter.ALL_MODEL_COST),
            new KeyValuePair<string, BuiltInParameter>("keynote", BuiltInParameter.KEYNOTE_PARAM),
            new KeyValuePair<string, BuiltInParameter>("mark", BuiltInParameter.ALL_MODEL_MARK),
            new KeyValuePair<string, BuiltInParameter>("url", BuiltInParameter.ALL_MODEL_URL),
            new KeyValuePair<string, BuiltInParameter>("description", BuiltInParameter.ALL_MODEL_DESCRIPTION)
        };

        public string Name => "set_material_identity";
        public string Description =>
            "Set a material's identity data: manufacturer, model, cost (number), keynote, mark, url, description, material_class, "
            + "material_category. All fields are set or none." + TypeEditGuard.Rule;
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + MaterialsHelpers.MaterialSchema + @",
            ""manufacturer"":{""type"":""string""},
            ""model"":{""type"":""string""},
            ""cost"":{""type"":""number""},
            ""keynote"":{""type"":""string""},
            ""mark"":{""type"":""string""},
            ""url"":{""type"":""string""},
            ""description"":{""type"":""string""},
            ""material_class"":{""type"":""string""},
            ""material_category"":{""type"":""string""},
            " + TypeEditGuard.SchemaFields + @"}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!MaterialsHelpers.TryMaterial(doc, p.Value<long?>("material_id"), p.Value<string>("material_name"),
                    "material_id", "material_name", out var mat, out var failure)) return failure;

            var materialClass = p.Value<string>("material_class");
            var materialCategory = p.Value<string>("material_category");
            var given = new List<KeyValuePair<string, BuiltInParameter>>();
            foreach (var field in Fields)
            {
                if (p[field.Key] == null || p[field.Key].Type == JTokenType.Null) continue;
                var parameter = mat.get_Parameter(field.Value);
                if (parameter == null || parameter.IsReadOnly)
                    return CommandResult.Fail("FIELD_NOT_WRITABLE: '" + field.Key + "' cannot be set on this material. Nothing changed.");
                given.Add(field);
            }
            if (given.Count == 0 && materialClass == null && materialCategory == null)
                return CommandResult.Fail("INVALID_PARAMS: nothing to change; give at least one identity field.");

            if (!TypeEditGuard.TryPlan(doc, p, new[] { new SharedEdit(mat) }, out var plan, out failure)) return failure;

            var result = new JObject { ["material_id"] = RevitCompat.GetId(mat.Id), ["name"] = mat.Name };
            using (var tx = new Transaction(doc, "B2M: Set material identity"))
            {
                tx.Start();
                try
                {
                    plan.Apply(doc);
                    mat = (Material)plan.TargetFor(mat);
                    if (materialClass != null) mat.MaterialClass = materialClass;
                    if (materialCategory != null) mat.MaterialCategory = materialCategory;
                    foreach (var field in given)
                    {
                        var parameter = mat.get_Parameter(field.Value);
                        var token = p[field.Key];
                        bool ok;
                        switch (parameter.StorageType)
                        {
                            case StorageType.Double:
                                ok = (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) && parameter.Set(token.Value<double>());
                                break;
                            case StorageType.String:
                                ok = parameter.Set(token.Type == JTokenType.String ? token.Value<string>() : token.ToString());
                                break;
                            default:
                                ok = false;
                                break;
                        }
                        if (!ok)
                        {
                            tx.RollBack();
                            return CommandResult.Fail("SET_FAILED: Revit rejected the value of '" + field.Key + "'. Nothing changed.");
                        }
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("SET_FAILED: could not change the material: " + ex.Message);
                }
            }

            result["material_id"] = RevitCompat.GetId(mat.Id);
            result["name"] = mat.Name;
            result["shared_edits"] = plan.Report();
            result["updated"] = true;
            result["material_class"] = mat.MaterialClass ?? "";
            result["material_category"] = mat.MaterialCategory ?? "";
            foreach (var field in given)
            {
                var parameter = mat.get_Parameter(field.Value);
                result[field.Key] = parameter.StorageType == StorageType.Double ? (JToken)parameter.AsDouble() : parameter.AsString();
            }
            return CommandResult.Ok(result);
        }
    }
}
