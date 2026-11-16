// New in B2M (PRD Step 9, T9): read_back_elements, the self-check's one read of a built model. For up to 5000 element ids it
// returns, per element, what the self-check compares with the build plan and the drawings, in mm, m² and degrees:
// category, type, family, Type Mark, level, location (point and rotation, or curve end points), and per kind the facts that
// kind is checked on - walls (width, base/top constraint and offsets, height), doors/windows (host, offset along the
// host wall from its start, width/height from the type, sill/head, facing/hand flipped), rooms (name, number, department,
// area, perimeter), floors and foundation slabs (sketch outline, computed area, thickness, height offset), structural
// columns/beams (section b/h from the type, levels and offsets), stairs (levels, risers, riser height, tread, run widths),
// railings (host), levels (elevation), grids (end points), shaft openings (outline, constraints, vertical extent) and
// model lines such as room separation lines (their curve). An id that is not in the document comes back as
// {id, exists: false}. Read-only.
// Responses are capped at 1 MiB, so a call returns as many elements as fit in about 900 000 characters and gives
// next_start; the caller repeats the call with start = next_start until next_start is null.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using B2M.Revit.Commands.Parameters;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Query
{
    public sealed class ReadBackElementsHandler : IRevitCommand
    {
        private const int MaxIds = 5000;
        private const int BudgetChars = 900000;
        private const double M2PerFt2 = 0.09290304;

        public string Name => "read_back_elements";
        public string Description => "Read built elements back for the self-check (mm, m2, degrees): per id the category, type, family, Type Mark, "
            + "level, location and the kind's own facts (walls, doors/windows, rooms, floors, columns/beams, stairs, railings, levels, "
            + "grids, shafts, model lines). Up to 5000 ids; pages by start/next_start under the 1 MiB response cap.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""},""description"":""1 to 5000 element ids""},
            ""start"":{""type"":""integer"",""description"":""index in element_ids to continue from (next_start of the previous call); default 0""}},
            ""required"":[""element_ids""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            var ids = (p["element_ids"] as JArray)?.Select(t => t.Value<long>()).ToArray();
            if (ids == null || ids.Length == 0 || ids.Length > MaxIds)
                return CommandResult.Fail("INVALID_PARAMS: give 1 to " + MaxIds + " element_ids.");
            var start = p.Value<int?>("start") ?? 0;
            if (start < 0 || start >= ids.Length) return CommandResult.Fail("INVALID_PARAMS: start must be from 0 to " + (ids.Length - 1) + ".");

            var elements = new JArray();
            var used = 0;
            var index = start;
            for (; index < ids.Length; index++)
            {
                JObject record;
                try
                {
                    var element = doc.GetElement(RevitCompat.ToElementId(ids[index]));
                    record = element == null ? new JObject { ["id"] = ids[index], ["exists"] = false } : Record(doc, element);
                }
                catch (Exception ex)
                {
                    record = new JObject { ["id"] = ids[index], ["exists"] = true, ["error"] = ex.Message };
                }
                var size = record.ToString(Formatting.None).Length + 1;
                if (used + size > BudgetChars && elements.Count > 0) break;
                elements.Add(record);
                used += size;
            }
            return CommandResult.Ok(new JObject
            {
                ["requested"] = ids.Length,
                ["start"] = start,
                ["returned"] = elements.Count,
                ["next_start"] = index < ids.Length ? new JValue(index) : JValue.CreateNull(),
                ["elements"] = elements
            });
        }

        private static JObject Record(Document doc, Element e)
        {
            var type = doc.GetElement(e.GetTypeId()) as ElementType;
            var record = new JObject
            {
                ["id"] = RevitCompat.GetId(e.Id),
                ["exists"] = true,
                ["category"] = Lookup.BuiltInName(e.Category),
                ["category_name"] = e.Category?.Name
            };
            Put(record, "type_id", QueryHelpers.IdOrNull(e.GetTypeId()));
            Put(record, "type_name", QueryHelpers.NameOf(type));
            Put(record, "family_name", type?.FamilyName);
            Put(record, "type_mark", type?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString());
            Put(record, "level", LevelName(doc, e.LevelId));
            Put(record, "location", Location(e));

            switch (e)
            {
                case Wall wall:
                    record["wall"] = WallFacts(doc, wall);
                    break;
                case Room room:
                    record["room"] = RoomFacts(room);
                    break;
                case Floor floor:
                    record["floor"] = FloorFacts(doc, floor);
                    break;
                case Stairs stairs:
                    record["stair"] = StairFacts(doc, stairs);
                    break;
                case Railing railing:
                    record["railing"] = Clean(new JObject { ["host_id"] = Id(railing.HostId) });
                    break;
                case Level level:
                    record["level_element"] = new JObject
                    {
                        ["name"] = level.Name,
                        ["elevation_mm"] = Mm.FromFeet(level.Elevation, 3),
                        ["project_elevation_mm"] = Mm.FromFeet(level.ProjectElevation, 3)
                    };
                    break;
                case Grid grid:
                    record["grid"] = GridFacts(grid);
                    break;
                case Opening opening:
                    record["shaft"] = ShaftFacts(doc, opening);
                    break;
                case CurveElement curve:
                    record["curve"] = Curve(curve.GeometryCurve);
                    break;
                case FamilyInstance instance:
                    FamilyFacts(doc, instance, type, record);
                    break;
            }
            return record;
        }

        // ------------------------------------------------------------------------------------------------ kinds

        private static JObject WallFacts(Document doc, Wall wall) => Clean(new JObject
        {
            ["width_mm"] = Mm.FromFeet(wall.Width, 3),
            ["base_level"] = ParameterLevel(doc, wall, BuiltInParameter.WALL_BASE_CONSTRAINT),
            ["base_offset_mm"] = Length(wall, BuiltInParameter.WALL_BASE_OFFSET),
            // Null when the top is unconnected (WALL_HEIGHT_TYPE holds no level): the height is then height_mm.
            ["top_level"] = ParameterLevel(doc, wall, BuiltInParameter.WALL_HEIGHT_TYPE),
            ["top_offset_mm"] = Length(wall, BuiltInParameter.WALL_TOP_OFFSET),
            ["height_mm"] = Length(wall, BuiltInParameter.WALL_USER_HEIGHT_PARAM),
            ["length_mm"] = Length(wall, BuiltInParameter.CURVE_ELEM_LENGTH)
        });

        private static JObject RoomFacts(Room room) => Clean(new JObject
        {
            ["name"] = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
            ["number"] = room.Number,
            ["department"] = room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString(),
            ["area_m2"] = Math.Round(room.Area * M2PerFt2, 4),
            ["perimeter_mm"] = Mm.FromFeet(room.Perimeter, 1),
            ["level"] = room.Level?.Name
        });

        private static JObject FloorFacts(Document doc, Floor floor)
        {
            var area = floor.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
            var thickness = Length(floor, BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM);
            if (thickness == null)
            {
                var structure = floor.FloorType?.GetCompoundStructure();
                if (structure != null) thickness = new JValue(Mm.FromFeet(structure.GetWidth(), 3));
            }
            JArray outline = null;
            if (doc.GetElement(floor.SketchId) is Sketch sketch)
            {
                outline = new JArray();
                foreach (CurveArray loop in sketch.Profile)
                    outline.Add(Loop(loop.Cast<Curve>()));
            }
            return Clean(new JObject
            {
                ["area_m2"] = area != null && area.HasValue ? new JValue(Math.Round(area.AsDouble() * M2PerFt2, 4)) : null,
                ["thickness_mm"] = thickness,
                ["height_offset_mm"] = Length(floor, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM),
                ["outline"] = outline
            });
        }

        private static JObject StairFacts(Document doc, Stairs stairs) => Clean(new JObject
        {
            ["base_level"] = ParameterLevel(doc, stairs, BuiltInParameter.STAIRS_BASE_LEVEL_PARAM),
            ["base_offset_mm"] = Length(stairs, BuiltInParameter.STAIRS_BASE_OFFSET),
            ["top_level"] = ParameterLevel(doc, stairs, BuiltInParameter.STAIRS_TOP_LEVEL_PARAM),
            ["top_offset_mm"] = Length(stairs, BuiltInParameter.STAIRS_TOP_OFFSET),
            ["height_mm"] = Mm.FromFeet(stairs.Height, 3),
            ["risers"] = stairs.ActualRisersNumber,
            ["riser_height_mm"] = Mm.FromFeet(stairs.ActualRiserHeight, 3),
            ["tread_mm"] = Mm.FromFeet(stairs.ActualTreadDepth, 3),
            ["run_widths_mm"] = new JArray(stairs.GetStairsRuns()
                .Select(id => doc.GetElement(id) as StairsRun).Where(r => r != null)
                .Select(r => Mm.FromFeet(r.ActualRunWidth, 3)))
        });

        private static JObject GridFacts(Grid grid)
        {
            var curve = grid.Curve;
            return Clean(new JObject
            {
                ["name"] = grid.Name,
                ["start"] = curve != null && curve.IsBound ? QueryHelpers.Point(curve.GetEndPoint(0)) : null,
                ["end"] = curve != null && curve.IsBound ? QueryHelpers.Point(curve.GetEndPoint(1)) : null
            });
        }

        private static JObject ShaftFacts(Document doc, Opening opening)
        {
            JArray outline;
            if (opening.IsRectBoundary)
            {
                var corners = opening.BoundaryRect;
                outline = new JArray(new JArray(corners.Select(Xy)));
            }
            else
            {
                outline = new JArray(Loop(opening.BoundaryCurves.Cast<Curve>()));
            }
            var box = opening.get_BoundingBox(null);
            return Clean(new JObject
            {
                ["outline"] = outline,
                ["base_level"] = ParameterLevel(doc, opening, BuiltInParameter.WALL_BASE_CONSTRAINT),
                ["base_offset_mm"] = Length(opening, BuiltInParameter.WALL_BASE_OFFSET),
                ["top_level"] = ParameterLevel(doc, opening, BuiltInParameter.WALL_HEIGHT_TYPE),
                ["top_offset_mm"] = Length(opening, BuiltInParameter.WALL_TOP_OFFSET),
                ["bottom_z_mm"] = box == null ? null : new JValue(Mm.FromFeet(box.Min.Z, 3)),
                ["top_z_mm"] = box == null ? null : new JValue(Mm.FromFeet(box.Max.Z, 3))
            });
        }

        private static void FamilyFacts(Document doc, FamilyInstance instance, ElementType type, JObject record)
        {
            var category = instance.Category == null ? BuiltInCategory.INVALID : instance.Category.BuiltInCategory;
            if (category == BuiltInCategory.OST_Doors || category == BuiltInCategory.OST_Windows)
            {
                var door = category == BuiltInCategory.OST_Doors;
                record["opening"] = Clean(new JObject
                {
                    ["host_id"] = Id(instance.Host?.Id),
                    ["host_offset_mm"] = HostOffset(instance),
                    ["width_mm"] = Size(instance, type, door ? BuiltInParameter.DOOR_WIDTH : BuiltInParameter.WINDOW_WIDTH,
                        BuiltInParameter.FAMILY_WIDTH_PARAM, "Width", "Rough Width"),
                    ["height_mm"] = Size(instance, type, door ? BuiltInParameter.DOOR_HEIGHT : BuiltInParameter.WINDOW_HEIGHT,
                        BuiltInParameter.FAMILY_HEIGHT_PARAM, "Height", "Rough Height"),
                    ["sill_mm"] = Length(instance, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM),
                    ["head_mm"] = Length(instance, BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM),
                    ["facing_flipped"] = instance.FacingFlipped,
                    ["hand_flipped"] = instance.HandFlipped
                });
            }
            else if (category == BuiltInCategory.OST_StructuralColumns || category == BuiltInCategory.OST_StructuralFraming)
            {
                record["frame"] = Clean(new JObject
                {
                    // The section as ensure_family_type sets it: type parameter b (else Width) and h (else Depth).
                    ["b_mm"] = TypeLength(type, "b", "Width"),
                    ["h_mm"] = TypeLength(type, "h", "Depth"),
                    ["base_level"] = ParameterLevel(doc, instance, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM),
                    ["base_offset_mm"] = Length(instance, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM),
                    ["top_level"] = ParameterLevel(doc, instance, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM),
                    ["top_offset_mm"] = Length(instance, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM),
                    ["reference_level"] = ParameterLevel(doc, instance, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)
                });
            }
        }

        // ------------------------------------------------------------------------------------------------ helpers

        /// <summary>Distance (mm, in plan) from the host wall's start to the instance's point along a straight host wall.</summary>
        private static JToken HostOffset(FamilyInstance instance)
        {
            if (!(instance.Host is Wall wall) || !(wall.Location is LocationCurve lc) || !(lc.Curve is Line line)) return null;
            if (!(instance.Location is LocationPoint lp)) return null;
            var start = line.GetEndPoint(0);
            var d = line.Direction;
            var planLength = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            if (planLength < 1e-9) return null;
            var along = ((lp.Point.X - start.X) * d.X + (lp.Point.Y - start.Y) * d.Y) / planLength;
            return new JValue(Mm.FromFeet(along, 3));
        }

        /// <summary>A door/window size: the type's built-in, then FAMILY_*, then by name; the instance's when the type has none.</summary>
        private static JToken Size(FamilyInstance instance, ElementType type, BuiltInParameter first, BuiltInParameter second, params string[] names)
        {
            foreach (var owner in new Element[] { type, instance })
            {
                if (owner == null) continue;
                var parameter = new[] { owner.get_Parameter(first), owner.get_Parameter(second) }
                    .Concat(names.Select(n => ParametersHelpers.FindByName(owner, n))).FirstOrDefault(IsLength);
                if (parameter != null) return new JValue(Mm.FromFeet(parameter.AsDouble(), 3));
            }
            return null;
        }

        private static JToken TypeLength(ElementType type, params string[] names)
        {
            if (type == null) return null;
            var parameter = names.Select(n => ParametersHelpers.FindByName(type, n)).FirstOrDefault(IsLength);
            return parameter == null ? null : new JValue(Mm.FromFeet(parameter.AsDouble(), 3));
        }

        private static bool IsLength(Parameter p)
        {
            if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return false;
            try { return SpecTypeId.Length.Equals(p.Definition?.GetDataType()); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return false; }
        }

        private static JToken Length(Element element, BuiltInParameter bip)
        {
            var parameter = element.get_Parameter(bip);
            return parameter != null && parameter.HasValue && parameter.StorageType == StorageType.Double
                ? new JValue(Mm.FromFeet(parameter.AsDouble(), 3))
                : null;
        }

        private static JToken ParameterLevel(Document doc, Element element, BuiltInParameter bip)
        {
            var parameter = element.get_Parameter(bip);
            if (parameter == null || parameter.StorageType != StorageType.ElementId) return null;
            return LevelName(doc, parameter.AsElementId());
        }

        private static JToken LevelName(Document doc, ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId) return null;
            var name = (doc.GetElement(id) as Level)?.Name;
            return name == null ? null : new JValue(name);
        }

        private static JToken Id(ElementId id) => QueryHelpers.IdOrNull(id) is long v ? new JValue(v) : null;

        private static JToken Location(Element e)
        {
            switch (e.Location)
            {
                case LocationPoint point:
                    double? rotation = null;
                    try
                    {
                        rotation = Math.Round(point.Rotation * 180 / Math.PI, 6);
                    }
                    catch
                    {
                        // Rooms have a point without a rotation.
                    }
                    return Clean(new JObject { ["point"] = QueryHelpers.Point(point.Point), ["rotation_deg"] = rotation });
                case LocationCurve curve when curve.Curve != null:
                    return Curve(curve.Curve);
                default:
                    return null;
            }
        }

        private static JObject Curve(Curve c)
        {
            if (c == null) return null;
            return Clean(new JObject
            {
                ["start"] = c.IsBound ? QueryHelpers.Point(c.GetEndPoint(0)) : null,
                ["end"] = c.IsBound ? QueryHelpers.Point(c.GetEndPoint(1)) : null,
                ["length_mm"] = Mm.FromFeet(c.Length, 3),
                ["straight"] = c is Line
            });
        }

        /// <summary>A loop's corners in plan (each curve's start point, in order).</summary>
        private static JArray Loop(IEnumerable<Curve> curves) => new JArray(curves.Where(c => c.IsBound).Select(c => Xy(c.GetEndPoint(0))));

        private static JObject Xy(XYZ point) => new JObject { ["x"] = Mm.FromFeet(point.X, 3), ["y"] = Mm.FromFeet(point.Y, 3) };

        private static void Put(JObject target, string key, object value)
        {
            if (value == null) return;
            if (value is JToken token)
            {
                if (token.Type != JTokenType.Null) target[key] = token;
                return;
            }
            target[key] = JToken.FromObject(value);
        }

        /// <summary>The object without its null members, to keep 5000 records under the response cap.</summary>
        private static JObject Clean(JObject o)
        {
            foreach (var property in o.Properties().ToList())
                if (property.Value == null || property.Value.Type == JTokenType.Null) property.Remove();
            return o;
        }
    }
}
