// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Parameters/ExportSharedParameterFileHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: read-only; it only returns the file's content as data, so rvt-mcp's unused "output: file"
// option is dropped and nothing is written. A missing file is an error instead of success with exists=false;
// bound state is matched by GUID through the project's SharedParameterElements (rvt-mcp matched ExternalDefinition
// keys only, which never occur in a project); a custom path is used only for this call and Revit's setting is restored.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Parameters
{
    public sealed class ExportSharedParameterFileHandler : IRevitCommand
    {
        private const int MaxRawLines = 20000;

        public string Name => "export_shared_parameter_file";
        public string Description =>
            "Return the shared parameter file (Revit's configured file, or sharedParameterFilePath) as structured data: groups and their definitions, "
            + "whether each is bound in the project, and optionally the raw lines. Read-only: nothing is written.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""sharedParameterFilePath"":{""type"":""string""},
            ""includeRawLines"":{""type"":""boolean""},
            ""includeBindings"":{""type"":""boolean""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            var withRaw = p.Value<bool?>("includeRawLines") ?? false;
            var withBindings = (p.Value<bool?>("includeBindings") ?? true) && doc != null && !doc.IsFamilyDocument;

            var bound = new HashSet<Guid>();
            if (withBindings)
                foreach (var kv in ParametersHelpers.Bindings(doc))
                {
                    var g = ParametersHelpers.SharedGuidOf(doc, kv.Key);
                    if (g.HasValue) bound.Add(g.Value);
                }

            if (!ParametersHelpers.SharedParameterFile.TryOpen(app.Application, p.Value<string>("sharedParameterFilePath"), false,
                    out var file, out _, out var failure)) return failure;
            using (file)
            {
                string[] lines;
                try { lines = File.ReadAllLines(file.Path); }
                catch (Exception ex) { return CommandResult.Fail("FILE_READ_FAILED: " + ex.Message); }

                var count = 0;
                var groups = new JArray(file.File.Groups.Cast<DefinitionGroup>().OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Select(g =>
                    new JObject
                    {
                        ["name"] = g.Name,
                        ["definitions"] = new JArray(g.Definitions.OfType<ExternalDefinition>().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(d =>
                        {
                            count++;
                            ForgeTypeId spec = null;
                            try { spec = d.GetDataType(); } catch { }
                            var o = new JObject
                            {
                                ["name"] = d.Name,
                                ["guid"] = d.GUID.ToString("d"),
                                ["dataTypeId"] = spec?.TypeId,
                                ["dataTypeLabel"] = ParametersHelpers.SpecLabel(spec),
                                ["description"] = d.Description,
                                ["visible"] = d.Visible,
                                ["userModifiable"] = d.UserModifiable,
                                ["hideWhenNoValue"] = d.HideWhenNoValue
                            };
                            if (withBindings) o["isBound"] = bound.Contains(d.GUID);
                            return o;
                        }))
                    }));

                var result = new JObject
                {
                    ["sharedParameterFilePath"] = file.Path,
                    ["lineCount"] = lines.Length,
                    ["groupCount"] = groups.Count,
                    ["definitionCount"] = count,
                    ["groups"] = groups
                };
                if (withRaw)
                {
                    result["rawLinesTruncated"] = lines.Length > MaxRawLines;
                    result["rawLines"] = new JArray(lines.Take(MaxRawLines));
                }
                return CommandResult.Ok(result);
            }
        }
    }
}
