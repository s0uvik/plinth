// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Links/GetLinkElementsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: category is a built-in category name (OST_Walls...), language independent, and an
// unknown name is an error (rvt-mcp matched localized display names and fell back to a name filter);
// limit defaults to 200, max 2000 (rvt-mcp 500/5000); offset added for paging; boxes in host mm via Mm.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Links
{
    public sealed class GetLinkElementsHandler : IRevitCommand
    {
        private const int DefaultLimit = 200;
        private const int MaxLimit = 2000;

        public string Name => "get_link_elements";
        public string Description =>
            "Read elements of a loaded Revit link through its instance (link_instance_id). category filters by built-in name such as OST_Walls. "
            + "limit defaults to 200 (max 2000); offset pages through the rest. include_bounding_box returns each element's box in host "
            + "coordinates (mm). The linked model is only read.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""link_instance_id"":{""type"":""integer""},
            ""category"":{""type"":""string""},
            ""limit"":{""type"":""integer""},
            ""offset"":{""type"":""integer""},
            ""include_bounding_box"":{""type"":""boolean""}},
            ""required"":[""link_instance_id""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var limit = p.Value<int?>("limit") ?? DefaultLimit;
            var offset = p.Value<int?>("offset") ?? 0;
            var includeBox = p.Value<bool?>("include_bounding_box") ?? false;
            if (limit < 1 || limit > MaxLimit) return CommandResult.Fail("INVALID_PARAMS: limit must be between 1 and " + MaxLimit + ".");
            if (offset < 0) return CommandResult.Fail("INVALID_PARAMS: offset must not be negative.");

            if (!LinksHelpers.TryRevitLinkInstance(doc, p.Value<long>("link_instance_id"), out var instance, out var failure)) return failure;
            var linkDoc = instance.GetLinkDocument();
            if (linkDoc == null)
                return CommandResult.Fail("LINK_NOT_LOADED: link instance " + RevitCompat.GetId(instance.Id) + " is unloaded; reload_link it first.");

            var collector = new FilteredElementCollector(linkDoc).WhereElementIsNotElementType();
            var category = p.Value<string>("category");
            if (!string.IsNullOrEmpty(category))
            {
                if (!Lookup.TryBuiltInCategory(category, out var bic, out failure)) return failure;
                collector = collector.OfCategory(bic);
            }

            List<Element> elements;
            try
            {
                // Skip elements without a category (internal bookkeeping objects).
                elements = collector.Where(e => e.Category != null).ToList();
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("READ_FAILED: could not read the linked model: " + ex.Message);
            }

            Transform toHost = Transform.Identity;
            try
            {
                toHost = instance.GetTotalTransform();
            }
            catch
            {
                // Identity: boxes are then in link coordinates, flagged below.
            }

            var warnings = new List<string>();
            var items = new JArray();
            foreach (var element in elements.Skip(offset).Take(limit))
            {
                string typeName = null, familyName = null;
                try
                {
                    var type = linkDoc.GetElement(element.GetTypeId()) as ElementType;
                    typeName = type?.Name;
                    familyName = type?.FamilyName;
                }
                catch
                {
                    // Left null.
                }
                var item = new JObject
                {
                    ["linked_element_id"] = RevitCompat.GetId(element.Id),
                    ["unique_id"] = element.UniqueId,
                    ["name"] = LinksHelpers.SafeName(element),
                    ["category"] = Lookup.BuiltInName(element.Category) ?? element.Category?.Name,
                    ["family"] = familyName,
                    ["type"] = typeName
                };
                if (includeBox)
                {
                    try
                    {
                        var box = element.get_BoundingBox(null);
                        if (box != null)
                        {
                            // Transform all eight corners so rotated links still get a box that holds the element.
                            var corners = new[] { box.Min.X, box.Max.X }.SelectMany(x => new[] { box.Min.Y, box.Max.Y }
                                .SelectMany(y => new[] { box.Min.Z, box.Max.Z }.Select(z => toHost.OfPoint(new XYZ(x, y, z))))).ToList();
                            item["host_bbox"] = new JObject
                            {
                                ["min"] = LinksHelpers.Xyz(new XYZ(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Min(c => c.Z))),
                                ["max"] = LinksHelpers.Xyz(new XYZ(corners.Max(c => c.X), corners.Max(c => c.Y), corners.Max(c => c.Z)))
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add("No bounding box for linked element " + RevitCompat.GetId(element.Id) + ": " + ex.Message);
                    }
                }
                items.Add(item);
            }

            return CommandResult.Ok(new JObject
            {
                ["link_instance"] = new JObject
                {
                    ["id"] = RevitCompat.GetId(instance.Id),
                    ["name"] = LinksHelpers.SafeName(instance),
                    ["type_id"] = RevitCompat.GetId(instance.GetTypeId())
                },
                ["linked_document"] = new JObject { ["title"] = linkDoc.Title, ["path"] = linkDoc.PathName },
                ["transform_to_host"] = LinksHelpers.TransformDto(toHost),
                ["total"] = elements.Count,
                ["offset"] = offset,
                ["returned"] = items.Count,
                ["truncated"] = offset + items.Count < elements.Count,
                ["elements"] = items,
                ["warnings"] = new JArray(warnings)
            });
        }
    }
}
