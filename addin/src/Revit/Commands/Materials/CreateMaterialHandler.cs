// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Materials/CreateMaterialHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: the duplicate-name check is case-insensitive (as Revit's own) and returns the existing id;
// the response reads the material back.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Materials
{
    public sealed class CreateMaterialHandler : IRevitCommand
    {
        public string Name => "create_material";
        public string Description => "Create a material with an optional class, category, shading colour (red, green, blue 0-255) and transparency (0-100).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""name"":{""type"":""string""},
            ""material_class"":{""type"":""string""},
            ""material_category"":{""type"":""string""},
            ""red"":{""type"":""integer""},""green"":{""type"":""integer""},""blue"":{""type"":""integer""},
            ""transparency"":{""type"":""integer""}},""required"":[""name""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var name = (p.Value<string>("name") ?? "").Trim();
            if (name.Length == 0) return CommandResult.Fail("INVALID_PARAMS: name must not be empty.");
            var existing = MaterialsHelpers.ByName(doc, name);
            if (existing != null)
                return CommandResult.Fail("NAME_IN_USE: a material named '" + existing.Name + "' already exists.",
                    new JObject { ["existing_material_id"] = RevitCompat.GetId(existing.Id) });
            if (!MaterialsHelpers.TryColor(p, out var color, out var failure)) return failure;
            if (!MaterialsHelpers.TryRange(p, "transparency", 0, 100, out var transparency, out failure)) return failure;
            var materialClass = p.Value<string>("material_class");
            var materialCategory = p.Value<string>("material_category");

            using (var tx = new Transaction(doc, "Plinth: Create material"))
            {
                tx.Start();
                try
                {
                    var mat = (Material)doc.GetElement(Material.Create(doc, name));
                    if (materialClass != null) mat.MaterialClass = materialClass;
                    if (materialCategory != null) mat.MaterialCategory = materialCategory;
                    if (color != null) mat.Color = color;
                    if (transparency.HasValue) mat.Transparency = transparency.Value;
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                    return CommandResult.Ok(new JObject
                    {
                        ["created"] = true,
                        ["material_id"] = RevitCompat.GetId(mat.Id),
                        ["name"] = mat.Name,
                        ["material_class"] = mat.MaterialClass ?? "",
                        ["material_category"] = mat.MaterialCategory ?? "",
                        ["color"] = MaterialsHelpers.ColorJson(mat.Color),
                        ["transparency"] = mat.Transparency
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the material: " + ex.Message);
                }
            }
        }
    }
}
