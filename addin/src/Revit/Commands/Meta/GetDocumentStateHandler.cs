// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Documents/GetDocumentStateHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: reports identity (title, file name only, never the full path), family/workshared/cloud/read-only/
// modified state and the Revit year. Checkpoints, backups, file-version GUIDs, disk space and the document-mode
// policy are not ported (no checkpoint tools in B2M). The document_id token is in the envelope's _target.

using System;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Meta
{
    public sealed class GetDocumentStateHandler : IRevitCommand
    {
        public string Name => "get_document_state";
        public string Description => "Describe the active document (or every open document with scope=all): title, file name, "
            + "family or project, workshared, cloud, read-only, unsaved changes, and the Revit year.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""scope"":{""type"":""string"",""enum"":[""active"",""all""]}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject parameters)
        {
            var scope = parameters.Value<string>("scope") ?? "active";
            var active = app.ActiveUIDocument?.Document;
            var documents = new JArray();
            if (scope == "active")
            {
                if (active != null) documents.Add(Describe(active, true));
            }
            else
            {
                foreach (Document doc in app.Application.Documents)
                {
                    try
                    {
                        if (doc.IsLinked) continue;
                        documents.Add(Describe(doc, active != null && doc.Equals(active)));
                    }
                    catch (Exception)
                    {
                        documents.Add(new JObject { ["title"] = SafeTitle(doc), ["error"] = "DOCUMENT_STATE_UNAVAILABLE" });
                    }
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["revit_year"] = app.Application.VersionNumber,
                ["revit_build"] = app.Application.VersionBuild,
                ["has_active_document"] = active != null,
                ["count"] = documents.Count,
                ["documents"] = documents
            });
        }

        private static JObject Describe(Document doc, bool isActive)
        {
            var path = doc.PathName;
            var workshared = doc.IsWorkshared;
            return new JObject
            {
                ["title"] = doc.Title,
                ["file_name"] = string.IsNullOrEmpty(path) ? null : Path.GetFileName(path),
                ["saved_to_disk"] = !string.IsNullOrEmpty(path),
                ["is_active"] = isActive,
                ["is_family"] = doc.IsFamilyDocument,
                ["is_workshared"] = workshared,
                ["central_file_name"] = workshared ? CentralFileName(doc) : null,
                ["is_cloud_model"] = SafeCloud(doc),
                ["is_read_only"] = doc.IsReadOnly,
                ["is_modified"] = doc.IsModified
            };
        }

        private static string CentralFileName(Document doc)
        {
            try
            {
                var central = doc.GetWorksharingCentralModelPath();
                if (central == null || central.Empty) return null;
                var visible = ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
                return Path.GetFileName(visible.Replace('/', '\\'));
            }
            catch
            {
                return null;
            }
        }

        private static bool? SafeCloud(Document doc)
        {
            try { return doc.IsModelInCloud; }
            catch { return null; }
        }

        private static string SafeTitle(Document doc)
        {
            try { return doc.Title; }
            catch { return null; }
        }
    }
}
