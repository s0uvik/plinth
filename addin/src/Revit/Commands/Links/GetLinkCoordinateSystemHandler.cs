// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Links/GetLinkCoordinateSystemHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: snapshot and DTO code shared through LinksHelpers; lengths through Mm; a miss returns
// valid_links (Revit links) and valid_cad_links; CAD imports (not links) are refused with that reason.

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Links
{
    public sealed class GetLinkCoordinateSystemHandler : IRevitCommand
    {
        public string Name => "get_link_coordinate_system";
        public string Description =>
            "Read how a Revit link or CAD link instance (link_instance_id) sits in the host: its transforms and where its origins land in host "
            + "internal and shared coordinates (mm, degrees). For a loaded Revit link also returns the linked model's own coordinate system "
            + "(internal origin, base points, project locations with ids for publish_coordinates_to_link).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""link_instance_id"":{""type"":""integer""}},
            ""required"":[""link_instance_id""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (doc.IsFamilyDocument) return CommandResult.Fail("NOT_A_PROJECT: links exist only in project documents.");

            var id = p.Value<long>("link_instance_id");
            var element = doc.GetElement(RevitCompat.ToElementId(id));
            var warnings = new List<string>();

            if (element is RevitLinkInstance revitLink)
            {
                var linkDoc = revitLink.GetLinkDocument();
                if (linkDoc == null)
                    return CommandResult.Fail("LINK_NOT_LOADED: link instance " + id + " is unloaded; reload_link it first.");
                var mapping = Mapping(revitLink, warnings, out var transforms);
                JObject linked = null;
                try
                {
                    linked = LinksHelpers.CoordinateSystem(linkDoc, warnings);
                    InternalOrigin linkedOrigin = null;
                    BasePoint linkedPbp = null, linkedSurvey = null;
                    try { linkedOrigin = InternalOrigin.Get(linkDoc); } catch { /* reported null */ }
                    try { linkedPbp = BasePoint.GetProjectBasePoint(linkDoc); } catch { /* reported null */ }
                    try { linkedSurvey = BasePoint.GetSurveyPoint(linkDoc); } catch { /* reported null */ }
                    var origins = new JObject
                    {
                        ["linked_internal_origin"] = MapPoint(doc, linkedOrigin?.Position, mapping, warnings, "linked internal origin"),
                        ["linked_project_base_point"] = MapPoint(doc, linkedPbp?.Position, mapping, warnings, "linked project base point"),
                        ["linked_survey_point"] = MapPoint(doc, linkedSurvey?.Position, mapping, warnings, "linked survey point")
                    };
                    return CommandResult.Ok(new JObject
                    {
                        ["link_instance"] = Describe(revitLink, "revit_link", linkDoc.Title),
                        ["units"] = new JObject { ["length"] = "mm", ["angle"] = "degrees" },
                        ["transforms"] = transforms,
                        ["origin_mapping_to_host"] = origins,
                        ["linked_document_coordinate_system"] = linked,
                        ["warnings"] = new JArray(warnings)
                    });
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail("READ_FAILED: could not read the linked model's coordinates: " + ex.Message);
                }
            }

            if (element is ImportInstance cad)
            {
                if (!cad.IsLinked)
                    return CommandResult.Fail("NOT_A_LINK: element " + id + " is an imported CAD drawing, not a CAD link; imports keep no link coordinates.",
                        new JObject { ["valid_cad_links"] = LinksHelpers.ValidCadLinks(doc) });
                var mapping = Mapping(cad, warnings, out var transforms);
                warnings.Add("CAD links expose only their origin and transforms; they have no base points or project locations.");
                return CommandResult.Ok(new JObject
                {
                    ["link_instance"] = Describe(cad, "cad_link", null),
                    ["units"] = new JObject { ["length"] = "mm", ["angle"] = "degrees" },
                    ["transforms"] = transforms,
                    ["origin_mapping_to_host"] = new JObject { ["cad_origin"] = MapPoint(doc, XYZ.Zero, mapping, warnings, "CAD origin") },
                    ["linked_document_coordinate_system"] = null,
                    ["warnings"] = new JArray(warnings)
                });
            }

            return CommandResult.Fail("LINK_NOT_FOUND: element " + id + " is not a Revit link instance or a CAD link.", new JObject
            {
                ["valid_links"] = LinksHelpers.ValidRevitLinks(doc),
                ["valid_cad_links"] = LinksHelpers.ValidCadLinks(doc)
            });
        }

        private static JObject Describe(Instance instance, string kind, string linkedTitle) => new JObject
        {
            ["id"] = RevitCompat.GetId(instance.Id),
            ["type_id"] = RevitCompat.GetId(instance.GetTypeId()),
            ["name"] = LinksHelpers.SafeName(instance),
            ["kind"] = kind,
            ["linked_document_title"] = linkedTitle
        };

        private static Transform Mapping(Instance instance, List<string> warnings, out JObject transforms)
        {
            Transform own = null, total = null;
            try { own = instance.GetTransform(); } catch (Exception ex) { warnings.Add("Could not read the instance transform: " + ex.Message); }
            try { total = instance.GetTotalTransform(); } catch (Exception ex) { warnings.Add("Could not read the total transform: " + ex.Message); }
            transforms = new JObject
            {
                ["instance_transform"] = LinksHelpers.TransformDto(own),
                ["total_transform"] = LinksHelpers.TransformDto(total),
                ["mapping_uses"] = total != null ? "total_transform" : "instance_transform"
            };
            return total ?? own;
        }

        private static JObject MapPoint(Document host, XYZ linkedPoint, Transform mapping, List<string> warnings, string label)
        {
            if (linkedPoint == null || mapping == null) return null;
            var hostPoint = mapping.OfPoint(linkedPoint);
            ProjectLocation active = null;
            try { active = host.ActiveProjectLocation; } catch { /* reported null */ }
            var shared = LinksHelpers.ProjectPosition(active, hostPoint, warnings, label);
            if (shared != null)
            {
                shared["project_location_id"] = RevitCompat.GetId(active.Id);
                shared["project_location_name"] = LinksHelpers.SafeName(active);
            }
            return new JObject
            {
                ["linked_internal_position_mm"] = LinksHelpers.Xyz(linkedPoint),
                ["host_internal_position_mm"] = LinksHelpers.Xyz(hostPoint),
                ["host_active_shared_position"] = shared
            };
        }
    }
}
