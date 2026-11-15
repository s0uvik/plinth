// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Links/ReloadLinkHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: Reload discards unsaved shared-coordinate changes in the link, which rvt-mcp did
// silently; B2M refuses then unless unsaved_positions is "save" (SavePositions into the linked .rvt first)
// or "discard". The load result is reported; misses return valid_links. Runs outside any transaction.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Links
{
    public sealed class ReloadLinkHandler : IRevitCommand
    {
        public string Name => "reload_link";
        public string Description =>
            "Load or reload a Revit link (by link_type_id or link_instance_id) from its stored path. Clears Revit's undo history. "
            + "If the link holds unsaved shared-coordinate changes it refuses unless unsaved_positions is \"save\" (writes them into the "
            + "linked .rvt file first) or \"discard\".";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""link_type_id"":{""type"":""integer""},
            ""link_instance_id"":{""type"":""integer""},
            ""unsaved_positions"":{""type"":""string"",""enum"":[""refuse"",""save"",""discard""]}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!LinksHelpers.TryLinkType(doc, p.Value<long?>("link_type_id"), p.Value<long?>("link_instance_id"), out var linkType, out var failure))
                return failure;
            if (linkType.IsNestedLink)
                return CommandResult.Fail("NESTED_LINK: '" + linkType.Name + "' is nested inside another link; reload the top-level link instead.");

            var unsaved = p.Value<string>("unsaved_positions") ?? "refuse";
            var path = LinksHelpers.ExternalPath(doc, linkType.Id, out _);
            var loadedBefore = RevitLinkType.IsLoaded(doc, linkType.Id);

            var hasChanges = false;
            try
            {
                hasChanges = loadedBefore && linkType.HasSaveablePositions();
            }
            catch
            {
                // Treated as no saveable changes.
            }
            if (hasChanges && unsaved == "refuse")
                return CommandResult.Fail("UNSAVED_POSITIONS: link '" + linkType.Name + "' has shared-coordinate changes not yet saved to its file; "
                    + "reloading would drop them. Pass unsaved_positions \"save\" to write them into " + (path ?? "the linked file")
                    + " first, or \"discard\". Nothing changed.");

            // Revit requires no open transaction for SavePositions or Reload.
            var positions = "none pending";
            if (hasChanges && unsaved == "save")
            {
                try
                {
                    linkType.SavePositions(new LinksHelpers.FixedSaveChoice(SaveModifiedLinksOptions.SaveLinks));
                    positions = "saved into the linked file";
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail("SAVE_POSITIONS_FAILED: could not save positions into " + (path ?? "the linked file") + ": " + ex.Message
                        + ". Nothing was reloaded.");
                }
            }
            else if (hasChanges)
            {
                positions = "discarded";
            }

            LinkLoadResult load;
            try
            {
                load = linkType.Reload();
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("RELOAD_FAILED: could not reload '" + linkType.Name + "' from " + (path ?? "its stored path") + ": " + ex.Message,
                    new JObject { ["positions"] = positions });
            }

            var loadedAfter = RevitLinkType.IsLoaded(doc, linkType.Id);
            var result = new JObject
            {
                ["link_type_id"] = RevitCompat.GetId(linkType.Id),
                ["name"] = linkType.Name,
                ["path"] = path,
                ["was_loaded"] = loadedBefore,
                ["is_loaded"] = loadedAfter,
                ["load_result"] = load?.LoadResult.ToString(),
                ["positions"] = positions,
                ["undo_history_cleared"] = true
            };
            return loadedAfter
                ? CommandResult.Ok(result)
                : CommandResult.Fail("RELOAD_FAILED: the link is not loaded after reloading (" + (load?.LoadResult.ToString() ?? "no result")
                    + "); check that " + (path ?? "its file") + " exists.", result);
        }
    }
}
