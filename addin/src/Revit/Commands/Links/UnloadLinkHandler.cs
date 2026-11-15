// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Links/UnloadLinkHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: rvt-mcp passed a null callback, which silently discards unsaved shared-coordinate
// changes in the link; B2M refuses when the link has such changes unless unsaved_positions is "save"
// (writes them into the linked .rvt first) or "discard". Misses return valid_links. Still runs outside
// any transaction, as Revit requires.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Links
{
    public sealed class UnloadLinkHandler : IRevitCommand
    {
        public string Name => "unload_link";
        public string Description =>
            "Unload a Revit link (by link_type_id or link_instance_id); all its instances disappear until reloaded. scope: all_users (default) "
            + "or local_user (workshared models only). Clears Revit's undo history. If the link holds unsaved shared-coordinate changes "
            + "(e.g. after publish_coordinates_to_link) it refuses unless unsaved_positions is \"save\" (writes them into the linked .rvt file) "
            + "or \"discard\".";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""link_type_id"":{""type"":""integer""},
            ""link_instance_id"":{""type"":""integer""},
            ""scope"":{""type"":""string"",""enum"":[""all_users"",""local_user""]},
            ""unsaved_positions"":{""type"":""string"",""enum"":[""refuse"",""save"",""discard""]}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!LinksHelpers.TryLinkType(doc, p.Value<long?>("link_type_id"), p.Value<long?>("link_instance_id"), out var linkType, out var failure))
                return failure;

            var scope = p.Value<string>("scope") ?? "all_users";
            var unsaved = p.Value<string>("unsaved_positions") ?? "refuse";
            var path = LinksHelpers.ExternalPath(doc, linkType.Id, out _);
            var result = new JObject
            {
                ["link_type_id"] = RevitCompat.GetId(linkType.Id),
                ["name"] = linkType.Name,
                ["path"] = path,
                ["scope"] = scope
            };

            if (linkType.IsNestedLink)
                return CommandResult.Fail("NESTED_LINK: '" + linkType.Name + "' is nested inside another link; unload the top-level link instead.");
            if (!RevitLinkType.IsLoaded(doc, linkType.Id))
            {
                result["unloaded"] = false;
                result["status"] = "already unloaded; nothing changed";
                return CommandResult.Ok(result);
            }
            if (scope == "local_user" && !doc.IsWorkshared)
                return CommandResult.Fail("NOT_WORKSHARED: local_user unload needs a workshared model; use scope all_users. Nothing changed.");

            var hasChanges = false;
            try
            {
                hasChanges = linkType.HasSaveablePositions();
            }
            catch
            {
                // Treated as no saveable changes.
            }
            if (hasChanges && unsaved == "refuse")
                return CommandResult.Fail("UNSAVED_POSITIONS: link '" + linkType.Name + "' has shared-coordinate changes not yet saved to its file. "
                    + "Pass unsaved_positions \"save\" to write them into " + (path ?? "the linked file") + " first, or \"discard\" to drop them. Nothing changed.");
            if (hasChanges && scope == "local_user" && unsaved == "save")
                return CommandResult.Fail("INVALID_PARAMS: saving positions while unloading for the local user only is not supported; "
                    + "unload for all_users, or save them first. Nothing changed.");

            // Revit requires no open transaction here.
            try
            {
                if (scope == "local_user")
                    linkType.UnloadLocally(null);
                else
                    linkType.Unload(new LinksHelpers.FixedSaveChoice(unsaved == "save" ? SaveModifiedLinksOptions.SaveLinks : SaveModifiedLinksOptions.DoNotSaveLinks));
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("UNLOAD_FAILED: could not unload '" + linkType.Name + "': " + ex.Message);
            }

            var loadedAfter = RevitLinkType.IsLoaded(doc, linkType.Id);
            result["unloaded"] = !loadedAfter;
            result["status"] = loadedAfter ? "still loaded" : scope == "local_user" ? "unloaded for this user only" : "unloaded for all users";
            result["had_unsaved_positions"] = hasChanges;
            result["positions"] = !hasChanges ? "none pending" : unsaved == "save" ? "saved into the linked file" : "discarded";
            result["undo_history_cleared"] = true;
            return loadedAfter
                ? CommandResult.Fail("UNLOAD_FAILED: Revit reported success but the link is still loaded.", result)
                : CommandResult.Ok(result);
        }
    }
}
