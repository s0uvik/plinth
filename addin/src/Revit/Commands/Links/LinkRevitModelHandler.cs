// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Links/LinkRevitModelHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: the path must be absolute (drive or UNC), an existing .rvt, and not the open document;
// only that file is read; reuse_existing_type compares full paths and skips types without a file reference
// (rvt-mcp threw on them); the transaction is "Plinth: ..." and rolled back only while open; the result reads
// back the new instance's transform in mm.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Links
{
    public sealed class LinkRevitModelHandler : IRevitCommand
    {
        public string Name => "link_revit_model";
        public string Description =>
            "Link a Revit model (.rvt) into the open project and place one instance. path must be an absolute path to an existing .rvt; only that file is read. "
            + "placement: origin (internal origin to internal origin, default) or shared (by shared coordinates). relative stores the path relative "
            + "to the host (default false). reuse_existing_type places another instance of a link type already pointing at that file instead of a new type.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""path"":{""type"":""string""},
            ""placement"":{""type"":""string"",""enum"":[""origin"",""shared""]},
            ""relative"":{""type"":""boolean""},
            ""reuse_existing_type"":{""type"":""boolean""}},
            ""required"":[""path""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (doc.IsFamilyDocument) return CommandResult.Fail("NOT_A_PROJECT: Revit links need a project document.");

            if (!LinksHelpers.TryInputFile(p.Value<string>("path"), new[] { ".rvt" }, out var path, out var failure)) return failure;
            if (!string.IsNullOrEmpty(doc.PathName) && string.Equals(SafeFull(doc.PathName), path, StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail("INVALID_PATH: " + path + " is the open document; a model cannot link itself.");

            var placement = p.Value<string>("placement") ?? "origin";
            var relative = p.Value<bool?>("relative") ?? false;
            var reuse = p.Value<bool?>("reuse_existing_type") ?? false;

            RevitLinkType existing = null;
            if (reuse)
            {
                existing = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>()
                    .FirstOrDefault(t =>
                    {
                        var linked = LinksHelpers.ExternalPath(doc, t.Id, out _);
                        return linked != null && string.Equals(SafeFull(linked), path, StringComparison.OrdinalIgnoreCase);
                    });
            }

            ElementId typeId;
            RevitLinkInstance instance;
            using (var tx = new Transaction(doc, "Plinth: Link Revit model"))
            {
                tx.Start();
                try
                {
                    if (existing != null)
                    {
                        typeId = existing.Id;
                    }
                    else
                    {
                        var result = RevitLinkType.Create(doc, ModelPathUtils.ConvertUserVisiblePathToModelPath(path), new RevitLinkOptions(relative));
                        typeId = result?.ElementId ?? ElementId.InvalidElementId;
                        if (typeId == ElementId.InvalidElementId)
                        {
                            tx.RollBack();
                            return CommandResult.Fail("LINK_FAILED: Revit could not load " + path + " (" + (result?.LoadResult.ToString() ?? "no result")
                                + "). Check that the file is a Revit model of this version or older and not locked. Nothing was added.");
                        }
                    }
                    instance = RevitLinkInstance.Create(doc, typeId, placement == "shared" ? ImportPlacement.Shared : ImportPlacement.Origin);
                    if (instance == null)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("LINK_FAILED: Revit did not place a link instance. Nothing was added.");
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the link back; see failures. Nothing was added.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("LINK_FAILED: could not link " + path + ": " + ex.Message);
                }
            }

            Transform transform = null;
            try
            {
                transform = instance.GetTotalTransform();
            }
            catch
            {
                // Reported as null.
            }
            return CommandResult.Ok(new JObject
            {
                ["link_type_id"] = RevitCompat.GetId(typeId),
                ["link_instance_id"] = RevitCompat.GetId(instance.Id),
                ["reused_existing_type"] = existing != null,
                ["name"] = LinksHelpers.SafeName(doc.GetElement(typeId)),
                ["path"] = path,
                ["placement"] = placement,
                ["relative_path"] = existing == null ? relative : (bool?)null,
                ["is_loaded"] = RevitLinkType.IsLoaded(doc, typeId),
                ["transform"] = LinksHelpers.TransformDto(transform)
            });
        }

        private static string SafeFull(string path)
        {
            try
            {
                return System.IO.Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }
    }
}
