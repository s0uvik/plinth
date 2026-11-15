// New in B2M (PRD "New tools"): rvt-mcp can move and rotate elements but not copy or mirror them.
// copy_elements copies by an offset, or to other levels the way Revit's "Paste aligned to selected levels" does:
// each copy is re-associated with the target level, keeping its offset from the level. mirror_elements mirrors
// about a vertical plane through a drawn axis, for mirrored flats. Both are all or nothing.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Modify
{
    public sealed class CopyElementsHandler : IRevitCommand
    {
        private const int MaxIds = 1000;
        private const int MaxCopies = 50;
        private static readonly double Tolerance = Mm.ToFeet(1.0);

        // Level parameter and the offset parameter that goes with it. Order matters only for reporting.
        private static readonly (BuiltInParameter Level, BuiltInParameter Offset)[] LevelPairs =
        {
            (BuiltInParameter.WALL_BASE_CONSTRAINT, BuiltInParameter.WALL_BASE_OFFSET),
            (BuiltInParameter.WALL_HEIGHT_TYPE, BuiltInParameter.WALL_TOP_OFFSET),
            (BuiltInParameter.LEVEL_PARAM, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM),
            (BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM),
            (BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM),
            (BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.INSTANCE_ELEVATION_PARAM),
        };

        public string Name => "copy_elements";
        public string Description => "Copy elements by dx_mm, dy_mm, dz_mm (count times, each further along), or to other levels with to_levels "
            + "(like Revit's paste aligned to selected levels: copies belong to the target level and keep their offset). "
            + "Hosted elements copy with their host when both are listed. All or nothing; returns the new element ids.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""dx_mm"":{""type"":""number""},""dy_mm"":{""type"":""number""},""dz_mm"":{""type"":""number""},
            ""count"":{""type"":""integer"",""description"":""Copies along the offset, default 1""},
            ""to_levels"":{""type"":""array"",""items"":{""type"":""string""},""description"":""Level names; use instead of an offset""}},
            ""required"":[""elementIds""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail(ModifyHelpers.NoDocument);
            if (!ModifyHelpers.TryIds(p, "elementIds", MaxIds, out var rawIds, out var failure)) return failure;
            if (!TryElements(doc, rawIds, out var elements, out failure)) return failure;
            var ids = elements.Select(e => e.Id).ToList();

            var levelNames = p["to_levels"]?.ToObject<string[]>() ?? new string[0];
            var hasOffset = p["dx_mm"] != null || p["dy_mm"] != null || p["dz_mm"] != null;
            if (levelNames.Length > 0 == hasOffset)
                return CommandResult.Fail("INVALID_PARAMS: give either an offset (dx_mm, dy_mm, dz_mm) or to_levels, not both.");

            return levelNames.Length > 0 ? ToLevels(doc, elements, ids, levelNames) : ByOffset(doc, ids, p);
        }

        private static CommandResult ByOffset(Document doc, List<ElementId> ids, JObject p)
        {
            var count = p.Value<int?>("count") ?? 1;
            if (count < 1 || count > MaxCopies) return CommandResult.Fail("INVALID_PARAMS: count must be 1 to " + MaxCopies + ".");
            var step = new XYZ(Mm.ToFeet(p.Value<double?>("dx_mm") ?? 0), Mm.ToFeet(p.Value<double?>("dy_mm") ?? 0), Mm.ToFeet(p.Value<double?>("dz_mm") ?? 0));
            if (new[] { step.X, step.Y, step.Z }.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                return CommandResult.Fail("INVALID_PARAMS: offsets must be finite numbers.");
            if (step.GetLength() < Tolerance)
                return CommandResult.Fail("INVALID_PARAMS: the offset is zero; copies would sit on top of the originals.");

            return ModifyHelpers.InTransaction(doc, "Copy elements", () =>
            {
                var copies = new JArray();
                for (var i = 1; i <= count; i++)
                {
                    var created = ElementTransformUtils.CopyElements(doc, ids, step * i);
                    copies.Add(new JObject { ["offset_mm"] = ModifyHelpers.Point(step * i), ["newIds"] = Ids(created) });
                }
                return CommandResult.Ok(new JObject { ["operation"] = "copy", ["sourceCount"] = ids.Count, ["copies"] = copies });
            });
        }

        private static CommandResult ToLevels(Document doc, List<Element> elements, List<ElementId> ids, string[] levelNames)
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var targets = new List<Level>();
            foreach (var name in levelNames.Distinct())
            {
                if (!Lookup.TryLevel(doc, name, out var level, out var miss)) return miss;
                targets.Add(level);
            }

            var sourceLevelIds = elements.Select(BaseLevelId).Distinct().ToList();
            var unlevelled = elements.Where(e => BaseLevelId(e) == ElementId.InvalidElementId).Select(e => RevitCompat.GetId(e.Id)).ToList();
            if (unlevelled.Count > 0)
                return CommandResult.Fail("UNSUPPORTED_ELEMENT: to_levels needs elements that belong to a level (walls, floors, columns, "
                    + "family instances). Use an offset for the others. Nothing was changed.", new { unsupportedIds = unlevelled });
            if (sourceLevelIds.Count != 1)
                return CommandResult.Fail("MIXED_LEVELS: to_levels copies from one level; these elements are on "
                    + sourceLevelIds.Count + " levels. Copy each level's elements separately. Nothing was changed.");
            var source = (Level)doc.GetElement(sourceLevelIds[0]);
            if (targets.Any(t => t.Id == source.Id))
                return CommandResult.Fail("INVALID_PARAMS: to_levels includes the elements' own level " + source.Name + ".");

            var sourceBottoms = elements.Select(Bottom).ToList();
            return ModifyHelpers.InTransaction(doc, "Copy elements to levels", () =>
            {
                var copies = new JArray();
                foreach (var target in targets)
                {
                    var delta = target.Elevation - source.Elevation;
                    var created = ElementTransformUtils.CopyElements(doc, ids, new XYZ(0, 0, delta));
                    var error = Relevel(doc, created, levels, delta);
                    if (error != null) return CommandResult.Fail(error);
                    doc.Regenerate();
                    error = Verify(doc, created, target, sourceBottoms, delta);
                    if (error != null) return CommandResult.Fail(error);
                    copies.Add(new JObject { ["level"] = target.Name, ["newIds"] = Ids(created) });
                }
                return CommandResult.Ok(new JObject
                {
                    ["operation"] = "copy_to_levels", ["sourceLevel"] = source.Name, ["sourceCount"] = ids.Count, ["copies"] = copies
                });
            });
        }

        /// <summary>
        /// The vertical copy left each copy on the source level with its offset raised by delta. Point every level
        /// parameter at the level delta higher and take delta back off its offset, so the copy stays where it is
        /// but belongs to the target level.
        /// </summary>
        private static string Relevel(Document doc, ICollection<ElementId> created, List<Level> levels, double delta)
        {
            foreach (var element in created.Select(doc.GetElement))
            {
                foreach (var (levelParam, offsetParam) in LevelPairs)
                {
                    var level = element.get_Parameter(levelParam);
                    if (level == null || level.IsReadOnly || level.StorageType != StorageType.ElementId) continue;
                    if (!(doc.GetElement(level.AsElementId()) is Level current)) continue; // e.g. an unconnected wall top
                    var moved = levels.FirstOrDefault(l => Math.Abs(l.Elevation - (current.Elevation + delta)) < Tolerance);
                    if (moved == null)
                        return "NO_MATCHING_LEVEL: element " + RevitCompat.GetId(element.Id) + " is tied to " + current.Name
                            + ", and no level sits the same height above the target. Nothing was changed.";
                    var offset = element.get_Parameter(offsetParam);
                    var before = offset != null && !offset.IsReadOnly ? offset.AsDouble() : (double?)null;
                    level.Set(moved.Id);
                    if (before.HasValue) offset.Set(before.Value - delta);
                }
            }
            return null;
        }

        private static string Verify(Document doc, ICollection<ElementId> created, Level target, List<double?> sourceBottoms, double delta)
        {
            var copies = created.Select(doc.GetElement).ToList();
            var wrong = copies.Where(c => BaseLevelId(c) != target.Id).Select(c => RevitCompat.GetId(c.Id)).ToList();
            if (wrong.Count > 0)
                return "NOT_RELEVELLED: copies " + string.Join(", ", wrong) + " did not end up on " + target.Name
                    + " (hosted elements follow their host; copy the host too). Nothing was changed.";
            var expected = sourceBottoms.Where(z => z.HasValue).Select(z => z.Value + delta).OrderBy(z => z).ToList();
            var actual = copies.Select(Bottom).Where(z => z.HasValue).Select(z => z.Value).OrderBy(z => z).ToList();
            if (expected.Count != actual.Count || expected.Zip(actual, (a, b) => Math.Abs(a - b)).Any(d => d > Tolerance))
                return "CONSTRAINED: Revit did not place the copies on " + target.Name + " at the same height as the originals. Nothing was changed.";
            return null;
        }

        private static ElementId BaseLevelId(Element element)
        {
            if (element.LevelId != ElementId.InvalidElementId) return element.LevelId;
            foreach (var bip in new[] { BuiltInParameter.WALL_BASE_CONSTRAINT, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,
                         BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM, BuiltInParameter.FAMILY_LEVEL_PARAM })
            {
                var id = element.get_Parameter(bip)?.AsElementId();
                if (id != null && id != ElementId.InvalidElementId) return id;
            }
            return ElementId.InvalidElementId;
        }

        private static double? Bottom(Element element) => element.get_BoundingBox(null)?.Min.Z;

        internal static bool TryElements(Document doc, long[] rawIds, out List<Element> elements, out CommandResult failure)
        {
            var found = rawIds.Select(id => doc.GetElement(RevitCompat.ToElementId(id))).ToList();
            elements = found;
            var missing = rawIds.Where((id, i) => found[i] == null).ToList();
            var refused = found.Where(e => e != null && (e is ElementType || e.GroupId != ElementId.InvalidElementId || e is Autodesk.Revit.DB.View))
                .Select(e => RevitCompat.GetId(e.Id)).ToList();
            failure = null;
            if (missing.Count > 0)
                failure = CommandResult.Fail("ELEMENT_NOT_FOUND: some elementIds do not exist. Nothing was changed.", new { missingIds = missing });
            else if (refused.Count > 0)
                failure = CommandResult.Fail("ELEMENT_REFUSED: types, views and members of groups cannot be copied or mirrored this way. Nothing was changed.",
                    new { refusedIds = refused });
            return failure == null;
        }

        internal static JArray Ids(IEnumerable<ElementId> ids) => new JArray(ids.Select(RevitCompat.GetId));
    }

    public sealed class MirrorElementsHandler : IRevitCommand
    {
        private const int MaxIds = 1000;

        public string Name => "mirror_elements";
        public string Description => "Mirror elements about a vertical plane through the axis from (x1_mm, y1_mm) to (x2_mm, y2_mm), "
            + "for mirrored flats. copy (default true) keeps the originals and returns the mirrored copies' ids. All or nothing.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""x1_mm"":{""type"":""number""},""y1_mm"":{""type"":""number""},
            ""x2_mm"":{""type"":""number""},""y2_mm"":{""type"":""number""},
            ""copy"":{""type"":""boolean""}},
            ""required"":[""elementIds"",""x1_mm"",""y1_mm"",""x2_mm"",""y2_mm""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail(ModifyHelpers.NoDocument);
            if (!ModifyHelpers.TryIds(p, "elementIds", MaxIds, out var rawIds, out var failure)) return failure;
            if (!CopyElementsHandler.TryElements(doc, rawIds, out var elements, out failure)) return failure;
            var copy = p.Value<bool?>("copy") ?? true;
            if (!copy && elements.Any(e => e.Pinned))
                return CommandResult.Fail("ELEMENT_REFUSED: pinned elements cannot be mirrored in place. Nothing was changed.",
                    new { refusedIds = elements.Where(e => e.Pinned).Select(e => RevitCompat.GetId(e.Id)).ToList() });

            var a = new XYZ(Mm.ToFeet(p.Value<double>("x1_mm")), Mm.ToFeet(p.Value<double>("y1_mm")), 0);
            var b = new XYZ(Mm.ToFeet(p.Value<double>("x2_mm")), Mm.ToFeet(p.Value<double>("y2_mm")), 0);
            if (new[] { a.X, a.Y, b.X, b.Y }.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                return CommandResult.Fail("INVALID_PARAMS: coordinates must be finite numbers.");
            var direction = b - a;
            if (direction.GetLength() < Mm.ToFeet(1.0))
                return CommandResult.Fail("INVALID_PARAMS: the axis end points are the same point.");

            var ids = elements.Select(e => e.Id).ToList();
            if (!ElementTransformUtils.CanMirrorElements(doc, ids))
                return CommandResult.Fail("CANNOT_MIRROR: Revit cannot mirror one or more of these elements. Nothing was changed.");
            var plane = Plane.CreateByNormalAndOrigin(direction.Normalize().CrossProduct(XYZ.BasisZ), a);

            return ModifyHelpers.InTransaction(doc, "Mirror elements", () =>
            {
                var created = ElementTransformUtils.MirrorElements(doc, ids, plane, copy);
                return CommandResult.Ok(new JObject
                {
                    ["operation"] = copy ? "mirror_copy" : "mirror",
                    ["count"] = ids.Count,
                    ["axis_mm"] = new JArray(ModifyHelpers.Point(a), ModifyHelpers.Point(b)),
                    ["newIds"] = copy ? CopyElementsHandler.Ids(created) : new JArray()
                });
            });
        }
    }
}
