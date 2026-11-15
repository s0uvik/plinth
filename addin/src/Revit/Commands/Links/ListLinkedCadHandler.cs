// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Links/ListLinkedCadHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: params come schema-checked; lengths through Mm; at most `limit` items (default 500,
// max 2000) with a truncated flag; owner view ids are null rather than 0 for model-wide instances.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Links
{
    public sealed class ListLinkedCadHandler : IRevitCommand
    {
        private const int DefaultLimit = 500;
        private const int MaxLimit = 2000;

        public string Name => "list_linked_cad";
        public string Description =>
            "List CAD links and CAD imports (DWG/DXF/DGN...) with link-or-import kind, status, path, owner view and origin in mm. "
            + "include_links and include_imports default to true; limit defaults to 500 (max 2000).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""include_imports"":{""type"":""boolean""},
            ""include_links"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var includeImports = p.Value<bool?>("include_imports") ?? true;
            var includeLinks = p.Value<bool?>("include_links") ?? true;
            var limit = p.Value<int?>("limit") ?? DefaultLimit;
            if (!includeImports && !includeLinks)
                return CommandResult.Fail("INVALID_PARAMS: include_imports and include_links cannot both be false.");
            if (limit < 1 || limit > MaxLimit)
                return CommandResult.Fail("INVALID_PARAMS: limit must be between 1 and " + MaxLimit + ".");

            var warnings = new List<string>();
            var matching = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
                .Where(i => i.IsLinked ? includeLinks : includeImports).ToList();

            var items = new JArray();
            foreach (var instance in matching.Take(limit))
            {
                var instanceId = RevitCompat.GetId(instance.Id);
                var type = doc.GetElement(instance.GetTypeId());
                string path = null;
                ExternalFileReference reference = null;
                if (type != null) path = LinksHelpers.ExternalPath(doc, type.Id, out reference);
                // Imported CAD has no file reference; a link without one has lost its file.
                var status = instance.IsLinked ? "Missing" : "Imported";
                if (reference != null)
                {
                    try
                    {
                        status = reference.GetLinkedFileStatus().ToString();
                    }
                    catch (Exception ex)
                    {
                        warnings.Add("Could not read the status of CAD instance " + instanceId + ": " + ex.Message);
                    }
                }

                JObject owner = null;
                if (instance.ViewSpecific)
                {
                    var view = doc.GetElement(instance.OwnerViewId) as Autodesk.Revit.DB.View;
                    owner = new JObject { ["id"] = RevitCompat.GetId(instance.OwnerViewId), ["name"] = view?.Name };
                }

                Transform transform = null;
                try
                {
                    transform = instance.GetTransform();
                }
                catch (Exception ex)
                {
                    warnings.Add("Could not read the transform of CAD instance " + instanceId + ": " + ex.Message);
                }

                items.Add(new JObject
                {
                    ["instance_id"] = instanceId,
                    ["type_id"] = RevitCompat.GetId(instance.GetTypeId()),
                    ["name"] = LinksHelpers.SafeName(type) ?? LinksHelpers.SafeName(instance),
                    ["kind"] = instance.IsLinked ? "cad_link" : "cad_import",
                    ["linked_file_status"] = status,
                    ["path"] = path,
                    ["view_specific"] = instance.ViewSpecific,
                    ["owner_view"] = owner,
                    ["origin_mm"] = transform == null ? null : LinksHelpers.Xyz(transform.Origin),
                    ["rotation_deg"] = transform == null ? (double?)null : LinksHelpers.Deg(Math.Atan2(transform.BasisX.Y, transform.BasisX.X))
                });
            }

            return CommandResult.Ok(new JObject
            {
                ["total"] = matching.Count,
                ["returned"] = items.Count,
                ["truncated"] = matching.Count > items.Count,
                ["counts"] = new JObject
                {
                    ["cad_links"] = matching.Count(i => i.IsLinked),
                    ["cad_imports"] = matching.Count(i => !i.IsLinked)
                },
                ["items"] = items,
                ["warnings"] = new JArray(warnings)
            });
        }
    }
}
