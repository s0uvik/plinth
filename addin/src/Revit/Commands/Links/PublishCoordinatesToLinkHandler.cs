// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Links/PublishCoordinatesToLinkHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: confirm is required and must be true (unchanged gate); the linked model's coordinate
// snapshot is returned before and after with a "changes" list and a summary; the result states whether the
// linked .rvt on disk was written: new save_to_link_file (default false) calls RevitLinkType.SavePositions
// after the commit, otherwise the change lives only in this Revit session until saved; a missing
// linked_project_location_id returns valid_locations; transaction "B2M: ..." rolled back only while open.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Links
{
    public sealed class PublishCoordinatesToLinkHandler : IRevitCommand
    {
        public string Name => "publish_coordinates_to_link";
        public string Description =>
            "DESTRUCTIVE: record the host project's shared coordinates (origin and true north) in a loaded Revit link, at that link "
            + "instance's current position, under the linked project location linked_project_location_id (default: the link's active "
            + "location; ids from get_link_coordinate_system). This changes the LINKED model's shared coordinates in Revit's memory; the "
            + ".rvt file on disk changes only with save_to_link_file: true (writes the linked file now) or when positions are saved later "
            + "(Revit asks on save, or unload_link/reload_link with unsaved_positions \"save\"). Needs confirm: true. Revit links only, not CAD.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""link_instance_id"":{""type"":""integer""},
            ""linked_project_location_id"":{""type"":""integer""},
            ""save_to_link_file"":{""type"":""boolean""},
            ""confirm"":{""type"":""boolean""}},
            ""required"":[""link_instance_id"",""confirm""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (doc.IsFamilyDocument) return CommandResult.Fail("NOT_A_PROJECT: shared coordinates exist only in project documents.");
            if (p.Value<bool>("confirm") != true)
                return CommandResult.Fail("CONFIRMATION_REQUIRED: publishing changes the linked model's shared coordinates; pass confirm: true to proceed. Nothing changed.");

            var id = p.Value<long>("link_instance_id");
            if (doc.GetElement(RevitCompat.ToElementId(id)) is ImportInstance)
                return CommandResult.Fail("NOT_SUPPORTED: element " + id + " is a CAD link or import; Revit can publish coordinates only to Revit links.",
                    new JObject { ["valid_links"] = LinksHelpers.ValidRevitLinks(doc) });
            if (!LinksHelpers.TryRevitLinkInstance(doc, id, out var instance, out var failure)) return failure;
            var linkDoc = instance.GetLinkDocument();
            if (linkDoc == null)
                return CommandResult.Fail("LINK_NOT_LOADED: link instance " + id + " is unloaded; reload_link it first. Nothing changed.");
            var linkType = doc.GetElement(instance.GetTypeId()) as RevitLinkType;
            if (linkType == null || linkType.IsNestedLink)
                return CommandResult.Fail("NESTED_LINK: link instance " + id + " is nested inside another link; publish to the top-level link. Nothing changed.");

            var locations = linkDoc.ProjectLocations.Cast<ProjectLocation>().ToList();
            ProjectLocation target;
            var locationId = p.Value<long?>("linked_project_location_id");
            if (locationId.HasValue)
            {
                target = locations.FirstOrDefault(l => RevitCompat.GetId(l.Id) == locationId.Value);
                if (target == null)
                    return CommandResult.Fail("LOCATION_NOT_FOUND: the linked model has no project location with id " + locationId.Value + ".",
                        new JObject
                        {
                            ["valid_locations"] = new JArray(locations.Select(l => new JObject
                            {
                                ["id"] = RevitCompat.GetId(l.Id),
                                ["name"] = LinksHelpers.SafeName(l)
                            }))
                        });
            }
            else
            {
                target = linkDoc.ActiveProjectLocation;
                if (target == null) return CommandResult.Fail("LOCATION_NOT_FOUND: the linked model has no active project location; pass linked_project_location_id.");
            }

            var saveToFile = p.Value<bool?>("save_to_link_file") ?? false;
            var warnings = new List<string>();
            var before = LinksHelpers.CoordinateSystem(linkDoc, warnings);

            using (var tx = new Transaction(doc, "B2M: Publish coordinates to link"))
            {
                tx.Start();
                try
                {
                    doc.PublishCoordinates(new LinkElementId(instance.Id, target.Id));
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the publish back; see failures. Nothing changed.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("PUBLISH_FAILED: Revit could not publish coordinates to link " + id + ": " + ex.Message + " Nothing changed.");
                }
            }

            var after = LinksHelpers.CoordinateSystem(linkDoc, warnings);
            var changes = LinksHelpers.Differences(before, after);
            var linkPath = LinksHelpers.ExternalPath(doc, linkType.Id, out _) ?? linkDoc.PathName;

            string fileState;
            string saveError = null;
            if (saveToFile)
            {
                try
                {
                    // Outside the transaction, as Revit requires; writes the linked .rvt on disk.
                    linkType.SavePositions(new LinksHelpers.FixedSaveChoice(SaveModifiedLinksOptions.SaveLinks));
                    fileState = "written: the new shared coordinates were saved into " + linkPath + ".";
                }
                catch (Exception ex)
                {
                    saveError = ex.Message;
                    fileState = "NOT written: saving into " + linkPath + " failed (" + ex.Message + "); the change exists only in this Revit session.";
                }
            }
            else
            {
                fileState = "not written: the change exists only in this Revit session. Save it with unload_link/reload_link unsaved_positions \"save\", "
                    + "or by choosing to save link positions when the host is saved; closing without saving drops it.";
            }

            var result = new JObject
            {
                ["published"] = true,
                ["model_changed"] = true,
                ["changed"] = "Linked model '" + linkDoc.Title + "' project location '" + LinksHelpers.SafeName(target)
                    + "' now records the host's shared coordinates at instance " + id + "'s position (" + changes.Count
                    + " value(s) changed in the linked model). Linked file " + fileState,
                ["link_instance"] = new JObject
                {
                    ["id"] = id,
                    ["name"] = LinksHelpers.SafeName(instance),
                    ["type_id"] = RevitCompat.GetId(linkType.Id)
                },
                ["linked_project_location"] = new JObject { ["id"] = RevitCompat.GetId(target.Id), ["name"] = LinksHelpers.SafeName(target) },
                ["linked_file"] = linkPath,
                ["linked_file_written"] = saveToFile && saveError == null,
                ["changes"] = new JArray(changes),
                ["linked_before"] = before,
                ["linked_after"] = after,
                ["warnings"] = new JArray(warnings)
            };
            return saveError == null
                ? CommandResult.Ok(result)
                : CommandResult.Fail("SAVE_POSITIONS_FAILED: coordinates WERE published in this session, but saving them into " + linkPath + " failed: " + saveError, result);
        }
    }
}
