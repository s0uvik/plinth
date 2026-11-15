// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Links/ListLinkedModelsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: params come schema-checked; lengths through Mm; transforms add rotation_deg; each type
// reports whether it holds unsaved shared-position changes (has_saveable_positions) and locally_unloaded.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Links
{
    public sealed class ListLinkedModelsHandler : IRevitCommand
    {
        public string Name => "list_linked_models";
        public string Description =>
            "List Revit link types and their instances: loaded/unloaded status, file path, attachment type and each instance's "
            + "transform (origin in mm, rotation in degrees). include_instances and include_unloaded default to true.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""include_instances"":{""type"":""boolean""},
            ""include_unloaded"":{""type"":""boolean""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var includeInstances = p.Value<bool?>("include_instances") ?? true;
            var includeUnloaded = p.Value<bool?>("include_unloaded") ?? true;
            var warnings = new List<string>();

            var instancesByType = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                .GroupBy(i => RevitCompat.GetId(i.GetTypeId())).ToDictionary(g => g.Key, g => g.ToList());

            var links = new JArray();
            var instanceCount = 0;
            foreach (var type in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().OrderBy(t => t.Name))
            {
                var typeId = RevitCompat.GetId(type.Id);
                var loaded = RevitLinkType.IsLoaded(doc, type.Id);
                if (!loaded && !includeUnloaded) continue;

                var path = LinksHelpers.ExternalPath(doc, type.Id, out var reference);
                string status = loaded ? "Loaded" : "Unloaded", pathType = null;
                if (reference != null)
                {
                    try
                    {
                        status = reference.GetLinkedFileStatus().ToString();
                        pathType = reference.GetPath()?.ServerPath == true ? "server" : reference.PathType.ToString().ToLowerInvariant();
                    }
                    catch (Exception ex)
                    {
                        warnings.Add("Could not read the file reference of link '" + type.Name + "': " + ex.Message);
                    }
                }

                var instances = new JArray();
                if (instancesByType.TryGetValue(typeId, out var list))
                {
                    foreach (var instance in list)
                    {
                        instanceCount++;
                        if (!includeInstances) continue;
                        Transform transform = null;
                        try
                        {
                            transform = instance.GetTotalTransform();
                        }
                        catch (Exception ex)
                        {
                            warnings.Add("Could not read the transform of link instance " + RevitCompat.GetId(instance.Id) + ": " + ex.Message);
                        }
                        instances.Add(new JObject
                        {
                            ["instance_id"] = RevitCompat.GetId(instance.Id),
                            ["name"] = LinksHelpers.SafeName(instance),
                            ["transform"] = LinksHelpers.TransformDto(transform)
                        });
                    }
                }

                bool? saveable = null;
                try
                {
                    saveable = loaded && !type.IsNestedLink ? type.HasSaveablePositions() : (bool?)null;
                }
                catch
                {
                    // Not answerable for this link; left null.
                }

                links.Add(new JObject
                {
                    ["link_type_id"] = typeId,
                    ["name"] = type.Name,
                    ["is_loaded"] = loaded,
                    ["locally_unloaded"] = type.LocallyUnloaded,
                    ["linked_file_status"] = status,
                    ["path"] = path,
                    ["path_type"] = pathType,
                    ["is_nested"] = type.IsNestedLink,
                    ["attachment_type"] = type.AttachmentType.ToString(),
                    ["has_saveable_positions"] = saveable,
                    ["instance_count"] = list?.Count ?? 0,
                    ["instances"] = includeInstances ? instances : null
                });
            }

            return CommandResult.Ok(new JObject
            {
                ["total_types"] = links.Count,
                ["total_instances"] = instanceCount,
                ["links"] = links,
                ["warnings"] = new JArray(warnings)
            });
        }
    }
}
