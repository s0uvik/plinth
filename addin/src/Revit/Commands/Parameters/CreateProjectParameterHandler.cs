// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Parameters/CreateProjectParameterHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: implemented. rvt-mcp's handler always failed because Revit's public API cannot create a
// non-shared project parameter. This one uses the standard route: a definition with a new GUID is written to a
// temporary shared parameter file under %TEMP%, bound to the categories, and the file is deleted afterwards. Revit's
// configured shared parameter file setting is switched only for the call and always restored; the user's own shared
// parameter file is never touched. The result is a project parameter that Revit stores as shared (it has a GUID),
// which behaves like a project parameter in schedules, tags excepted.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Parameters
{
    public sealed class CreateProjectParameterHandler : IRevitCommand
    {
        public string Name => "create_project_parameter";
        public string Description =>
            "Create a project parameter bound to categories (OST_Doors or display names) as instance or type, with dataTypeId (text, integer, "
            + "number, length, area, volume, angle, yesno, url, material, or a Forge spec id) and parameterGroupId (default Data). Revit's API "
            + "only creates shared-backed parameters, so it gets a new GUID through a temporary file; your shared parameter file is not changed.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""name"":{""type"":""string""},
            ""dataTypeId"":{""type"":""string""},
            ""categories"":{""type"":""array"",""items"":{""type"":""string""}},
            ""bindingKind"":{""type"":""string"",""enum"":[""instance"",""type""]},
            ""parameterGroupId"":{""type"":""string""},
            ""description"":{""type"":""string""}},""required"":[""name"",""dataTypeId"",""categories""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!ParametersHelpers.RequireProject(doc, out var failure)) return failure;
            var name = (p.Value<string>("name") ?? "").Trim();
            if (name.Length == 0) return CommandResult.Fail("INVALID_PARAMS: name must not be empty.");
            if (!ParametersHelpers.TryDataType(p.Value<string>("dataTypeId"), out var spec, out failure)) return failure;
            if (!ParametersHelpers.TryGroup(p.Value<string>("parameterGroupId"), out var groupId, out failure)) return failure;
            var kind = p.Value<string>("bindingKind") ?? "instance";
            var names = p["categories"].ToObject<string[]>();
            if (names.Length == 0) return CommandResult.Fail("INVALID_PARAMS: categories must not be empty.");
            var categories = new List<Category>();
            foreach (var n in names)
            {
                if (!ParametersHelpers.TryBindableCategory(doc, n, out var c, out failure)) return failure;
                if (categories.All(x => x.Id != c.Id)) categories.Add(c);
            }
            var clash = ParametersHelpers.Bindings(doc).FirstOrDefault(kv => string.Equals(kv.Key.Name, name, StringComparison.OrdinalIgnoreCase));
            if (clash.Key != null)
                return CommandResult.Fail("NAME_IN_USE: the project already has a parameter named '" + clash.Key.Name + "'.",
                    new JObject { ["guid"] = ParametersHelpers.SharedGuidOf(doc, clash.Key)?.ToString("d") });

            var tempPath = Path.Combine(Path.GetTempPath(), "plinth-project-parameter-" + Guid.NewGuid().ToString("N") + ".txt");
            Guid guid;
            try
            {
                if (!ParametersHelpers.SharedParameterFile.TryOpen(app.Application, tempPath, true, out var file, out _, out failure)) return failure;
                using (file)
                using (var tx = new Transaction(doc, "Plinth: Create project parameter"))
                {
                    var definition = (ExternalDefinition)file.File.Groups.Create("Plinth").Definitions.Create(
                        new ExternalDefinitionCreationOptions(name, spec) { Description = p.Value<string>("description") ?? "" });
                    guid = definition.GUID;
                    tx.Start();
                    try
                    {
                        var set = app.Application.Create.NewCategorySet();
                        foreach (var c in categories) set.Insert(c);
                        Binding binding = kind == "type"
                            ? (Binding)app.Application.Create.NewTypeBinding(set)
                            : app.Application.Create.NewInstanceBinding(set);
                        if (!doc.ParameterBindings.Insert(definition, binding, groupId))
                        {
                            tx.RollBack();
                            return CommandResult.Fail("BIND_FAILED: Revit refused to add the parameter '" + name + "'.");
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
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("CREATE_FAILED: could not prepare the parameter definition: " + ex.Message);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch { /* a leftover temp file is harmless */ }
            }

            var bound = ParametersHelpers.Bindings(doc).FirstOrDefault(kv => ParametersHelpers.SharedGuidOf(doc, kv.Key) == guid);
            return CommandResult.Ok(new JObject
            {
                ["created"] = true,
                ["name"] = name,
                ["guid"] = guid.ToString("d"),
                ["sharedBacked"] = true,
                ["dataTypeId"] = spec.TypeId,
                ["dataTypeLabel"] = ParametersHelpers.SpecLabel(spec),
                ["bindingKind"] = kind,
                ["parameterGroupId"] = groupId.TypeId,
                ["categories"] = ParametersHelpers.CategoriesJson(bound.Value)
            });
        }
    }
}
