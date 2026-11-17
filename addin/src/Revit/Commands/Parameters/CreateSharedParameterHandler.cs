// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Parameters/CreateSharedParameterHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: no silent default file (rvt-mcp created %LOCALAPPDATA%\Bimwright\rvt-mcp\shared-parameters.txt when
// Revit had none set); a missing file is created only with createFileIfMissing: true (default now false), as an empty
// file Revit initialises itself; the user's configured shared parameter file is restored on every path (rvt-mcp's
// early returns left it pointing at the custom file); data types resolve to real Revit specs (rvt-mcp passed
// unversioned ids such as "autodesk.spec.aec:int"); a name clash in any group is reported; default group is "Plinth".

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Parameters
{
    public sealed class CreateSharedParameterHandler : IRevitCommand
    {
        public string Name => "create_shared_parameter";
        public string Description =>
            "Add a shared parameter definition to the shared parameter file (Revit's configured file, or sharedParameterFilePath for this call; "
            + "Revit's setting is restored afterwards). dataTypeId: text, integer, number, length, area, volume, angle, yesno, url, material, "
            + "or a Forge spec id. Writes the file; does not bind it (use bind_shared_parameter).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""name"":{""type"":""string""},
            ""dataTypeId"":{""type"":""string""},
            ""groupName"":{""type"":""string""},
            ""guid"":{""type"":""string""},
            ""sharedParameterFilePath"":{""type"":""string""},
            ""createFileIfMissing"":{""type"":""boolean""},
            ""description"":{""type"":""string""},
            ""visible"":{""type"":""boolean""},
            ""userModifiable"":{""type"":""boolean""},
            ""hideWhenNoValue"":{""type"":""boolean""}},""required"":[""name"",""dataTypeId""]}";
        // Writes the shared parameter file.
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var name = (p.Value<string>("name") ?? "").Trim();
            if (name.Length == 0) return CommandResult.Fail("INVALID_PARAMS: name must not be empty.");
            if (!ParametersHelpers.TryDataType(p.Value<string>("dataTypeId"), out var spec, out var failure)) return failure;
            var groupName = string.IsNullOrWhiteSpace(p.Value<string>("groupName")) ? "Plinth" : p.Value<string>("groupName").Trim();
            var guid = Guid.NewGuid();
            if (p["guid"] != null && !ParametersHelpers.TryGuid(p.Value<string>("guid"), out guid, out failure)) return failure;

            if (!ParametersHelpers.SharedParameterFile.TryOpen(app.Application, p.Value<string>("sharedParameterFilePath"),
                    p.Value<bool?>("createFileIfMissing") ?? false, out var file, out var fileCreated, out failure)) return failure;
            using (file)
            {
                var existing = file.Find(guid, out var owner);
                if (existing != null)
                {
                    if (string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase) && spec.Equals(existing.GetDataType()))
                        return CommandResult.Ok(Describe(file.Path, false, true, fileCreated, owner, existing));
                    return CommandResult.Fail("GUID_IN_USE: GUID " + guid + " is already '" + existing.Name + "' in group '" + owner.Name + "'.");
                }
                foreach (DefinitionGroup g in file.File.Groups)
                    foreach (Definition d in g.Definitions)
                        if (string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                            return CommandResult.Fail("NAME_IN_USE: the file already has a parameter named '" + d.Name + "' in group '" + g.Name + "'.",
                                new JObject { ["existing_guid"] = (d as ExternalDefinition)?.GUID.ToString("d") });

                try
                {
                    var group = file.File.Groups.get_Item(groupName) ?? file.File.Groups.Create(groupName);
                    var options = new ExternalDefinitionCreationOptions(name, spec)
                    {
                        GUID = guid,
                        Description = p.Value<string>("description") ?? "",
                        Visible = p.Value<bool?>("visible") ?? true,
                        UserModifiable = p.Value<bool?>("userModifiable") ?? true,
                        HideWhenNoValue = p.Value<bool?>("hideWhenNoValue") ?? false
                    };
                    var created = (ExternalDefinition)group.Definitions.Create(options);
                    return CommandResult.Ok(Describe(file.Path, true, false, fileCreated, group, created));
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail("CREATE_FAILED: could not add the parameter to " + file.Path + ": " + ex.Message);
                }
            }
        }

        private static JObject Describe(string path, bool created, bool existed, bool fileCreated, DefinitionGroup group, ExternalDefinition d)
        {
            var spec = d.GetDataType();
            return new JObject
            {
                ["created"] = created,
                ["alreadyExisted"] = existed,
                ["sharedParameterFilePath"] = path,
                ["fileCreated"] = fileCreated,
                ["groupName"] = group.Name,
                ["name"] = d.Name,
                ["guid"] = d.GUID.ToString("d"),
                ["dataTypeId"] = spec?.TypeId,
                ["dataTypeLabel"] = ParametersHelpers.SpecLabel(spec),
                ["visible"] = d.Visible,
                ["userModifiable"] = d.UserModifiable,
                ["hideWhenNoValue"] = d.HideWhenNoValue
            };
        }
    }
}
