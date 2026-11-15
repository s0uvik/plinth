// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), files
// src/shared/Handlers/Links/*.cs and src/shared/Handlers/Documents/GetProjectCoordinateSystemHandler.cs, commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for B2M: the per-handler copies of link lookup, path checks, point/transform DTOs and the
// coordinate-system snapshot are gathered here; lengths go through Mm; link misses return valid_links.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Links
{
    internal static class LinksHelpers
    {
        private const int MaxOptions = 100;
        private const double RadToDeg = 180.0 / Math.PI;

        // ---------- input files ----------

        /// <summary>An absolute, existing file with one of the given extensions. Nothing else is read.</summary>
        public static bool TryInputFile(string path, string[] extensions, out string fullPath, out CommandResult failure)
        {
            fullPath = null;
            failure = null;
            if (!IsAbsolute(path))
            {
                failure = CommandResult.Fail("INVALID_PATH: path must be an absolute path such as C:\\models\\site.rvt.");
                return false;
            }
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                failure = CommandResult.Fail("INVALID_PATH: " + ex.Message);
                return false;
            }
            var extension = (Path.GetExtension(fullPath) ?? "").ToLowerInvariant();
            if (!extensions.Contains(extension))
            {
                failure = CommandResult.Fail("INVALID_PATH: '" + extension + "' files are not supported here; use " + string.Join(" or ", extensions) + ".");
                return false;
            }
            if (!File.Exists(fullPath))
            {
                failure = CommandResult.Fail("FILE_NOT_FOUND: " + fullPath + " does not exist.");
                return false;
            }
            return true;
        }

        private static bool IsAbsolute(string path) =>
            !string.IsNullOrEmpty(path)
            && ((path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
                || path.StartsWith(@"\\", StringComparison.Ordinal));

        // ---------- link lookup ----------

        /// <summary>A Revit link type from exactly one of a type id or an instance id.</summary>
        public static bool TryLinkType(Document doc, long? typeId, long? instanceId, out RevitLinkType linkType, out CommandResult failure)
        {
            linkType = null;
            failure = null;
            if (typeId.HasValue == instanceId.HasValue)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: give exactly one of link_type_id or link_instance_id.",
                    new JObject { ["valid_links"] = ValidRevitLinks(doc) });
                return false;
            }
            if (typeId.HasValue)
            {
                linkType = doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as RevitLinkType;
                if (linkType == null)
                    failure = CommandResult.Fail("LINK_NOT_FOUND: no Revit link type has id " + typeId.Value + ".",
                        new JObject { ["valid_links"] = ValidRevitLinks(doc) });
                return linkType != null;
            }
            if (!TryRevitLinkInstance(doc, instanceId.Value, out var instance, out failure)) return false;
            linkType = doc.GetElement(instance.GetTypeId()) as RevitLinkType;
            if (linkType == null)
                failure = CommandResult.Fail("LINK_NOT_FOUND: link instance " + instanceId.Value + " has no Revit link type.");
            return linkType != null;
        }

        public static bool TryRevitLinkInstance(Document doc, long id, out RevitLinkInstance instance, out CommandResult failure)
        {
            failure = null;
            instance = doc.GetElement(RevitCompat.ToElementId(id)) as RevitLinkInstance;
            if (instance != null) return true;
            failure = CommandResult.Fail("LINK_NOT_FOUND: element " + id + " is not a Revit link instance.",
                new JObject { ["valid_links"] = ValidRevitLinks(doc) });
            return false;
        }

        /// <summary>Every Revit link type with its instances, for error data.</summary>
        public static JArray ValidRevitLinks(Document doc)
        {
            var instances = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                .GroupBy(i => RevitCompat.GetId(i.GetTypeId())).ToDictionary(g => g.Key, g => g.ToList());
            return new JArray(new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>()
                .OrderBy(t => t.Name).Take(MaxOptions).Select(t =>
                {
                    var typeId = RevitCompat.GetId(t.Id);
                    return new JObject
                    {
                        ["link_type_id"] = typeId,
                        ["name"] = t.Name,
                        ["is_loaded"] = RevitLinkType.IsLoaded(doc, t.Id),
                        ["link_instance_ids"] = new JArray(instances.TryGetValue(typeId, out var list)
                            ? list.Select(i => RevitCompat.GetId(i.Id)) : Enumerable.Empty<long>())
                    };
                }));
        }

        /// <summary>Linked CAD instances (not imports), for error data.</summary>
        public static JArray ValidCadLinks(Document doc) =>
            new JArray(new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
                .Where(i => i.IsLinked).Take(MaxOptions).Select(i => new JObject
                {
                    ["instance_id"] = RevitCompat.GetId(i.Id),
                    ["name"] = SafeName(doc.GetElement(i.GetTypeId())) ?? SafeName(i)
                }));

        /// <summary>The user-visible path of an external file reference, or null.</summary>
        public static string ExternalPath(Document doc, ElementId typeId, out ExternalFileReference reference)
        {
            reference = null;
            try
            {
                if (!ExternalFileUtils.IsExternalFileReference(doc, typeId)) return null;
                reference = ExternalFileUtils.GetExternalFileReference(doc, typeId);
                var modelPath = reference?.GetPath();
                return modelPath == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath);
            }
            catch
            {
                return null;
            }
        }

        public static string SafeName(Element element)
        {
            try
            {
                return element?.Name;
            }
            catch
            {
                return null;
            }
        }

        // ---------- geometry DTOs (mm, degrees) ----------

        public static double Len(double feet) => Mm.FromFeet(feet, 3);

        public static double Deg(double radians) => Math.Round(radians * RadToDeg, 6);

        public static double ToRadians(double degrees) => degrees / RadToDeg;

        public static JObject Xyz(XYZ p) => p == null ? null : new JObject
        {
            ["x_mm"] = Len(p.X),
            ["y_mm"] = Len(p.Y),
            ["z_mm"] = Len(p.Z)
        };

        public static JObject Shared(XYZ p) => p == null ? null : new JObject
        {
            ["east_west_mm"] = Len(p.X),
            ["north_south_mm"] = Len(p.Y),
            ["elevation_mm"] = Len(p.Z)
        };

        private static JObject Basis(XYZ v) => new JObject
        {
            ["x"] = Math.Round(v.X, 9),
            ["y"] = Math.Round(v.Y, 9),
            ["z"] = Math.Round(v.Z, 9)
        };

        public static JObject TransformDto(Transform t) => t == null ? null : new JObject
        {
            ["origin_mm"] = Xyz(t.Origin),
            ["basis_x"] = Basis(t.BasisX),
            ["basis_y"] = Basis(t.BasisY),
            ["basis_z"] = Basis(t.BasisZ),
            ["rotation_deg"] = Deg(Math.Atan2(t.BasisX.Y, t.BasisX.X)),
            ["is_identity"] = t.IsIdentity,
            ["has_reflection"] = t.HasReflection
        };

        /// <summary>Where an internal point sits in a project location's shared coordinates.</summary>
        public static JObject ProjectPosition(ProjectLocation location, XYZ internalPoint, List<string> warnings, string label)
        {
            if (location == null || internalPoint == null) return null;
            try
            {
                using (var position = location.GetProjectPosition(internalPoint))
                {
                    if (position == null) return null;
                    return new JObject
                    {
                        ["east_west_mm"] = Len(position.EastWest),
                        ["north_south_mm"] = Len(position.NorthSouth),
                        ["elevation_mm"] = Len(position.Elevation),
                        ["angle_to_true_north_deg"] = Deg(position.Angle)
                    };
                }
            }
            catch (Exception ex)
            {
                warnings?.Add("Could not compute shared coordinates for " + label + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>A base point's displayed E/W, N/S, elevation and angle parameters.</summary>
        public static JObject BasePointValues(BasePoint point)
        {
            if (point == null) return null;
            return new JObject
            {
                ["east_west_mm"] = LengthParam(point, BuiltInParameter.BASEPOINT_EASTWEST_PARAM),
                ["north_south_mm"] = LengthParam(point, BuiltInParameter.BASEPOINT_NORTHSOUTH_PARAM),
                ["elevation_mm"] = LengthParam(point, BuiltInParameter.BASEPOINT_ELEVATION_PARAM),
                ["angle_to_true_north_deg"] = AngleParam(point, BuiltInParameter.BASEPOINT_ANGLETON_PARAM)
            };
        }

        private static JToken LengthParam(Element e, BuiltInParameter id)
        {
            var p = e.get_Parameter(id);
            return p == null || !p.HasValue ? JValue.CreateNull() : new JValue(Len(p.AsDouble()));
        }

        private static JToken AngleParam(Element e, BuiltInParameter id)
        {
            var p = e.get_Parameter(id);
            return p == null || !p.HasValue ? JValue.CreateNull() : new JValue(Deg(p.AsDouble()));
        }

        // ---------- coordinate-system snapshot ----------

        /// <summary>
        /// Internal origin, project base point, survey point and every project location of a project document.
        /// Used by get_project_coordinate_system, get_link_coordinate_system and the coordinate writers' before/after.
        /// </summary>
        public static JObject CoordinateSystem(Document doc, List<string> warnings)
        {
            InternalOrigin origin = null;
            BasePoint pbp = null, survey = null;
            ProjectLocation active = null;
            try { origin = InternalOrigin.Get(doc); } catch (Exception ex) { warnings.Add("Could not read the internal origin: " + ex.Message); }
            try { pbp = BasePoint.GetProjectBasePoint(doc); } catch (Exception ex) { warnings.Add("Could not read the project base point: " + ex.Message); }
            try { survey = BasePoint.GetSurveyPoint(doc); } catch (Exception ex) { warnings.Add("Could not read the survey point: " + ex.Message); }
            try { active = doc.ActiveProjectLocation; } catch (Exception ex) { warnings.Add("Could not read the active project location: " + ex.Message); }

            var locations = new List<ProjectLocation>();
            try
            {
                locations = doc.ProjectLocations.Cast<ProjectLocation>().OrderBy(l => SafeName(l), StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex)
            {
                warnings.Add("Could not list project locations: " + ex.Message);
                if (active != null) locations.Add(active);
            }
            var activeId = active == null ? (long?)null : RevitCompat.GetId(active.Id);

            return new JObject
            {
                ["document_title"] = doc.Title,
                ["units"] = new JObject { ["length"] = "mm", ["angle"] = "degrees", ["latitude_longitude"] = "degrees" },
                ["active_project_location"] = active == null ? null : new JObject
                {
                    ["id"] = RevitCompat.GetId(active.Id),
                    ["name"] = SafeName(active)
                },
                ["internal_origin"] = origin == null ? null : new JObject
                {
                    ["element_id"] = RevitCompat.GetId(origin.Id),
                    ["internal_position_mm"] = Xyz(origin.Position),
                    ["active_shared_position_mm"] = Shared(origin.SharedPosition)
                },
                ["project_base_point"] = BasePointDto(pbp, false),
                ["survey_point"] = BasePointDto(survey, true),
                ["project_locations"] = new JArray(locations.GroupBy(l => RevitCompat.GetId(l.Id)).Select(g => g.First()).Select(l =>
                {
                    var label = "project location '" + SafeName(l) + "'";
                    JObject site = null;
                    try
                    {
                        var s = l.GetSiteLocation();
                        if (s != null)
                            site = new JObject
                            {
                                ["place_name"] = s.PlaceName ?? "",
                                ["latitude_deg"] = Math.Round(s.Latitude * RadToDeg, 8),
                                ["longitude_deg"] = Math.Round(s.Longitude * RadToDeg, 8),
                                ["elevation_mm"] = Len(s.Elevation),
                                ["time_zone_hours"] = Math.Round(s.TimeZone, 2)
                            };
                    }
                    catch (Exception ex)
                    {
                        warnings.Add("Could not read site data for " + label + ": " + ex.Message);
                    }
                    return new JObject
                    {
                        ["id"] = RevitCompat.GetId(l.Id),
                        ["name"] = SafeName(l),
                        ["is_active"] = activeId.HasValue && activeId.Value == RevitCompat.GetId(l.Id),
                        ["internal_origin_in_shared"] = ProjectPosition(l, XYZ.Zero, warnings, label),
                        ["project_base_point_in_shared"] = pbp == null ? null : ProjectPosition(l, pbp.Position, warnings, label),
                        ["survey_point_in_shared"] = survey == null ? null : ProjectPosition(l, survey.Position, warnings, label),
                        ["site"] = site
                    };
                }))
            };
        }

        private static JObject BasePointDto(BasePoint point, bool isSurvey)
        {
            if (point == null) return null;
            var dto = new JObject
            {
                ["element_id"] = RevitCompat.GetId(point.Id),
                ["is_shared"] = point.IsShared,
                ["internal_position_mm"] = Xyz(point.Position),
                ["active_shared_position_mm"] = Shared(point.SharedPosition),
                ["displayed_parameters"] = BasePointValues(point)
            };
            if (isSurvey) dto["clipped"] = point.Clipped;
            return dto;
        }

        /// <summary>
        /// The parts of a coordinate snapshot that a coordinate command can change, flattened to
        /// "path: before -> after" lines. Empty when nothing changed.
        /// </summary>
        public static List<string> Differences(JToken before, JToken after, string prefix = "")
        {
            var changes = new List<string>();
            if (before is JObject bo && after is JObject ao)
            {
                foreach (var name in bo.Properties().Select(p => p.Name).Union(ao.Properties().Select(p => p.Name)))
                    changes.AddRange(Differences(bo[name], ao[name], prefix.Length == 0 ? name : prefix + "." + name));
                return changes;
            }
            if (before is JArray ba && after is JArray aa)
            {
                // Project locations are matched by id so a new or removed location shows up by name.
                var bById = ba.OfType<JObject>().Where(o => o["id"] != null).ToDictionary(o => o.Value<long>("id"));
                var aById = aa.OfType<JObject>().Where(o => o["id"] != null).ToDictionary(o => o.Value<long>("id"));
                if (bById.Count == ba.Count && aById.Count == aa.Count)
                {
                    foreach (var id in bById.Keys.Union(aById.Keys))
                    {
                        bById.TryGetValue(id, out var b);
                        aById.TryGetValue(id, out var a);
                        var label = prefix + "[" + ((a ?? b).Value<string>("name") ?? id.ToString()) + "]";
                        if (b == null) changes.Add(label + ": added");
                        else if (a == null) changes.Add(label + ": removed");
                        else changes.AddRange(Differences(b, a, label));
                    }
                    return changes;
                }
            }
            if (IsNumber(before) && IsNumber(after))
            {
                if (Math.Abs(before.Value<double>() - after.Value<double>()) > 0.0005)
                    changes.Add(prefix + ": " + before + " -> " + after);
                return changes;
            }
            if (!JToken.DeepEquals(before ?? JValue.CreateNull(), after ?? JValue.CreateNull()))
                changes.Add(prefix + ": " + (before?.ToString(Newtonsoft.Json.Formatting.None) ?? "null")
                    + " -> " + (after?.ToString(Newtonsoft.Json.Formatting.None) ?? "null"));
            return changes;
        }

        private static bool IsNumber(JToken t) => t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer);

        /// <summary>Answers Revit's "save changed link positions?" question with a fixed choice.</summary>
        public sealed class FixedSaveChoice : ISaveSharedCoordinatesCallback
        {
            private readonly SaveModifiedLinksOptions _choice;

            public FixedSaveChoice(SaveModifiedLinksOptions choice) => _choice = choice;

            public SaveModifiedLinksOptions GetSaveModifiedLinksOption(RevitLinkType link) => _choice;
        }
    }
}
