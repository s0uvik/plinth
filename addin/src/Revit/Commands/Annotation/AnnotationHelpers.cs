// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), the private helpers repeated in
// src/shared/Handlers/Annotations/*.cs (EnsureViewCanHostAnnotation, ResolveCategory, GetTaggedElementIds),
// commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: one shared copy; a view id goes through Lookup.TryView; a category is matched by its exact
// name or BuiltInCategory name only (rvt-mcp also matched any OST_ name ending with the input) and a miss lists
// close matches; tagged ids come from GetTaggedLocalElementIds (Revit 2024+, no reflection); view-plane points
// in plan views are model X/Y in mm, as everywhere else in B2M.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Annotation
{
    // B2M.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    internal static class AnnotationHelpers
    {
        private const int MaxOptions = 100;

        /// <summary>The view by id, else the active view; refuses templates and views that cannot hold annotation.</summary>
        public static bool TryAnnotationView(Document doc, long? viewId, out View view, out CommandResult failure)
        {
            if (viewId.HasValue)
            {
                if (!Lookup.TryView(doc, viewId.Value, out view, out failure)) return false;
            }
            else
            {
                view = doc.ActiveView;
                failure = null;
                if (view == null)
                {
                    failure = CommandResult.Fail("NO_ACTIVE_VIEW: the document has no active view; pass view_id.");
                    return false;
                }
                if (view.IsTemplate)
                {
                    failure = CommandResult.Fail("VIEW_IS_TEMPLATE: the active view is a view template; pass view_id.");
                    view = null;
                    return false;
                }
            }
            var vt = view.ViewType;
            if (vt == ViewType.Schedule || vt == ViewType.ColumnSchedule || vt == ViewType.PanelSchedule
                || vt == ViewType.ProjectBrowser || vt == ViewType.SystemBrowser || vt == ViewType.Legend
                || vt == ViewType.Internal || vt == ViewType.Undefined)
            {
                failure = CommandResult.Fail("VIEW_NOT_ANNOTATABLE: view '" + view.Name + "' is a " + vt + " and cannot hold this annotation.");
                view = null;
                return false;
            }
            return true;
        }

        /// <summary>A category by display name (any case), BuiltInCategory name (OST_Walls), or that name without "OST_".</summary>
        public static bool TryCategory(Document doc, string input, out Category category, out CommandResult failure)
        {
            failure = null;
            category = null;
            var text = (input ?? "").Trim();
            if (text.Length > 0)
            {
                foreach (Category c in doc.Settings.Categories)
                    if (string.Equals(c.Name, text, StringComparison.OrdinalIgnoreCase)) { category = c; break; }
                if (category == null)
                {
                    var bicName = text.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) ? text : "OST_" + text;
                    if (Enum.TryParse(bicName, true, out BuiltInCategory bic) && Enum.IsDefined(typeof(BuiltInCategory), bic))
                    {
                        try { category = Category.GetCategory(doc, bic); }
                        catch (Exception) { category = null; }
                    }
                }
            }
            if (category != null) return true;

            var close = new List<string>();
            foreach (Category c in doc.Settings.Categories)
                if (text.Length > 0 && c.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) close.Add(c.Name);
            failure = CommandResult.Fail("CATEGORY_NOT_FOUND: no category named '" + input
                    + "'. Use the category name (Walls, Doors) or the built-in name (OST_Walls).",
                new JObject { ["close_matches"] = new JArray(close.OrderBy(n => n).Take(MaxOptions)) });
            return false;
        }

        /// <summary>A tag family type by id, or null when no id is given.</summary>
        public static bool TryTagType(Document doc, long? typeId, out FamilySymbol tagType, out CommandResult failure)
        {
            tagType = null;
            failure = null;
            if (!typeId.HasValue) return true;
            tagType = doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as FamilySymbol;
            if (tagType != null && tagType.Category != null && tagType.Category.CategoryType == CategoryType.Annotation) return true;
            tagType = null;
            failure = CommandResult.Fail("TYPE_NOT_FOUND: tag_type_id " + typeId.Value + " is not an annotation (tag) family type.");
            return false;
        }

        /// <summary>Host element ids tagged by an IndependentTag (local elements only).</summary>
        public static IEnumerable<long> TaggedIds(IndependentTag tag)
        {
            ICollection<ElementId> ids;
            try { ids = tag.GetTaggedLocalElementIds(); }
            catch (Exception) { return Enumerable.Empty<long>(); }
            return ids.Where(id => id != ElementId.InvalidElementId).Select(RevitCompat.GetId);
        }

        /// <summary>Ids of every element tagged in the view: independent tags, room tags and area tags.</summary>
        public static HashSet<long> TaggedInView(Document doc, View view)
        {
            var result = new HashSet<long>();
            foreach (var tag in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
                foreach (var id in TaggedIds(tag)) result.Add(id);
            foreach (var tag in new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_RoomTags)
                .WhereElementIsNotElementType().OfType<RoomTag>())
                if (!tag.IsTaggingLink && tag.Room != null) result.Add(RevitCompat.GetId(tag.Room.Id));
            foreach (var tag in new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_AreaTags)
                .WhereElementIsNotElementType().OfType<AreaTag>())
                if (tag.Area != null) result.Add(RevitCompat.GetId(tag.Area.Id));
            return result;
        }

        /// <summary>Where a tag goes: the location point, the location curve's middle, else the bounding box centre; null if none.</summary>
        public static XYZ Anchor(Element element, View view)
        {
            if (element.Location is LocationPoint lp) return lp.Point;
            if (element.Location is LocationCurve lc && lc.Curve != null) return lc.Curve.Evaluate(0.5, true);
            var box = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
            return box == null ? null : (box.Min + box.Max) * 0.5;
        }

        /// <summary>
        /// Tags one element at a point: rooms get a room tag (plan views only), anything else an IndependentTag.
        /// With a tag type it is placed with that type; without, Revit's default tag for the category.
        /// Throws with a readable message when Revit refuses; the caller records it per element.
        /// </summary>
        public static Element CreateTag(Document doc, View view, Element element, FamilySymbol tagType, bool leader,
            TagOrientation orientation, XYZ point)
        {
            if (element is Room room)
            {
                if (!(view is ViewPlan)) throw new InvalidOperationException("room tags need a plan view.");
                if (tagType != null && tagType.Category?.BuiltInCategory != BuiltInCategory.OST_RoomTags)
                    throw new InvalidOperationException("a room needs a room tag type.");
                var roomTag = doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(point.X, point.Y), view.Id);
                if (roomTag == null) throw new InvalidOperationException("Revit returned no room tag.");
                roomTag.HasLeader = leader;
                roomTag.TagOrientation = orientation == TagOrientation.Vertical
                    ? SpatialElementTagOrientation.Vertical : SpatialElementTagOrientation.Horizontal;
                if (tagType != null && roomTag.GetTypeId() != tagType.Id) roomTag.ChangeTypeId(tagType.Id);
                return roomTag;
            }
            var reference = new Reference(element);
            var tag = tagType != null
                ? IndependentTag.Create(doc, tagType.Id, view.Id, reference, leader, orientation, point)
                : IndependentTag.Create(doc, view.Id, reference, leader, TagMode.TM_ADDBY_CATEGORY, orientation, point);
            if (tag == null) throw new InvalidOperationException("Revit returned no tag (is a tag family for this category loaded?).");
            return tag;
        }

        public static XYZ HeadPosition(Element tag) =>
            tag is SpatialElementTag spatial ? spatial.TagHeadPosition : (tag as IndependentTag)?.TagHeadPosition;

        /// <summary>
        /// A point on the view plane from x/y in mm. Plan views: model X/Y at the view origin's height.
        /// Other views: along the view's right/up directions from the view origin.
        /// </summary>
        public static XYZ ViewPoint(View view, double xMm, double yMm)
        {
            var x = Mm.ToFeet(xMm);
            var y = Mm.ToFeet(yMm);
            if (view is ViewPlan) return new XYZ(x, y, view.Origin.Z);
            return view.Origin + view.RightDirection * x + view.UpDirection * y;
        }

        public static JObject PointMm(XYZ p) => new JObject
        {
            ["x"] = Mm.FromFeet(p.X),
            ["y"] = Mm.FromFeet(p.Y),
            ["z"] = Mm.FromFeet(p.Z)
        };

        /// <summary>A {x, y} object of numbers inside a nested parameter; the schema checker does not look inside objects.</summary>
        public static bool TryXy(JToken token, string what, out double x, out double y, out CommandResult failure)
        {
            x = y = 0;
            failure = null;
            var o = token as JObject;
            var tx = o?["x"];
            var ty = o?["y"];
            if (IsNumber(tx) && IsNumber(ty))
            {
                x = tx.Value<double>();
                y = ty.Value<double>();
                return true;
            }
            failure = CommandResult.Fail("INVALID_PARAMS: " + what + " must be {\"x\": mm, \"y\": mm}.");
            return false;
        }

        public static bool IsNumber(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float);

        public static bool TryLimit(JObject p, int fallback, int max, out int limit, out CommandResult failure)
        {
            limit = p.Value<int?>("limit") ?? fallback;
            failure = null;
            if (limit >= 1 && limit <= max) return true;
            failure = CommandResult.Fail("INVALID_PARAMS: limit must be 1 to " + max + ".");
            return false;
        }
    }
}
