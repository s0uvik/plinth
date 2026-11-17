// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Links/AcquireCoordinatesFromLinkHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: confirm is required and must be true (unchanged gate); the result carries a full
// coordinate snapshot before and after (base points, survey point, every project location, site) plus a
// "changes" list and a plain-language summary of what changed, instead of only the project base point;
// misses return valid_links and valid_cad_links; transaction "Plinth: ..." rolled back only while open.

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Links
{
    public sealed class AcquireCoordinatesFromLinkHandler : IRevitCommand
    {
        public string Name => "acquire_coordinates_from_link";
        public string Description =>
            "DESTRUCTIVE: make the open project take its shared coordinates from a Revit link or CAD link instance (link_instance_id): "
            + "the link's shared origin and true north become the host's, based on where the instance sits; geolocation (site) is "
            + "overwritten from the link. Model geometry does not move. Needs confirm: true. Returns the coordinate system before and "
            + "after and a list of what changed.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""link_instance_id"":{""type"":""integer""},
            ""confirm"":{""type"":""boolean""}},
            ""required"":[""link_instance_id"",""confirm""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (doc.IsFamilyDocument) return CommandResult.Fail("NOT_A_PROJECT: shared coordinates exist only in project documents.");
            if (p.Value<bool>("confirm") != true)
                return CommandResult.Fail("CONFIRMATION_REQUIRED: acquiring coordinates replaces the project's shared coordinate system and "
                    + "geolocation; pass confirm: true to proceed. Nothing changed.");

            var id = p.Value<long>("link_instance_id");
            var element = doc.GetElement(RevitCompat.ToElementId(id));
            string kind;
            if (element is RevitLinkInstance revitLink)
            {
                if (revitLink.GetLinkDocument() == null)
                    return CommandResult.Fail("LINK_NOT_LOADED: link instance " + id + " is unloaded; reload_link it first. Nothing changed.");
                kind = "revit_link";
            }
            else if (element is ImportInstance cad && cad.IsLinked)
            {
                kind = "cad_link";
            }
            else
            {
                return CommandResult.Fail("LINK_NOT_FOUND: element " + id + (element is ImportInstance
                        ? " is an imported CAD drawing; coordinates can only be acquired from a CAD link or Revit link."
                        : " is not a Revit link instance or a CAD link."),
                    new JObject { ["valid_links"] = LinksHelpers.ValidRevitLinks(doc), ["valid_cad_links"] = LinksHelpers.ValidCadLinks(doc) });
            }

            var warnings = new List<string>();
            var before = LinksHelpers.CoordinateSystem(doc, warnings);

            using (var tx = new Transaction(doc, "Plinth: Acquire coordinates from link"))
            {
                tx.Start();
                try
                {
                    doc.AcquireCoordinates(element.Id);
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the coordinate change back; see failures. Nothing changed.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("ACQUIRE_FAILED: Revit could not acquire coordinates from element " + id + ": " + ex.Message + " Nothing changed.");
                }
            }

            var after = LinksHelpers.CoordinateSystem(doc, warnings);
            var changes = LinksHelpers.Differences(before, after);
            return CommandResult.Ok(new JObject
            {
                ["acquired"] = true,
                ["model_changed"] = true,
                ["changed"] = changes.Count == 0
                    ? "Coordinates acquired, but the host's shared coordinates already matched the link; no values changed."
                    : "The host project's shared coordinate system now comes from " + kind.Replace('_', ' ') + " '" + LinksHelpers.SafeName(element)
                      + "' (" + changes.Count + " value(s) changed; internal coordinates and model geometry unchanged). Save the project to keep it.",
                ["source"] = new JObject { ["id"] = id, ["kind"] = kind, ["name"] = LinksHelpers.SafeName(element) },
                ["changes"] = new JArray(changes),
                ["before"] = before,
                ["after"] = after,
                ["warnings"] = new JArray(warnings)
            });
        }
    }
}
