// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Structural/TagStructuralFramingHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: the tag type is chosen by tag_type_id or tag_type_name (+ tag_family_name), else Revit's default
// structural framing tag (rvt-mcp took the first tag type found); view templates are refused; members already tagged in
// the view are skipped; element_ids that are not framing are reported instead of dropped; per-element failures carry
// the reason; when nothing could be tagged the transaction rolls back and the command fails.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Structural
{
    using View = Autodesk.Revit.DB.View;

    public sealed class TagStructuralFramingHandler : IRevitCommand
    {
        public string Name => "tag_structural_framing";
        public string Description =>
            "Tag structural framing (beams, braces) at their midpoints in a view (view_id, default the active view). Optional element_ids "
            + "(default every framing member visible in the view); members already tagged in the view are skipped. Tag type by tag_type_id "
            + "or tag_type_name (+ tag_family_name), default the project's structural framing tag.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""view_id"":{""type"":""integer""},
            ""tag_type_id"":{""type"":""integer""},
            ""tag_type_name"":{""type"":""string""},
            ""tag_family_name"":{""type"":""string""},
            ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            View view;
            CommandResult failure;
            var viewId = p.Value<long?>("view_id");
            if (viewId.HasValue)
            {
                if (!Lookup.TryView(doc, viewId.Value, out view, out failure)) return failure;
            }
            else
            {
                view = doc.ActiveView;
                if (view == null) return CommandResult.Fail("NO_ACTIVE_VIEW: the document has no active view; pass view_id.");
            }

            FamilySymbol tagType = null;
            var typeId = p.Value<long?>("tag_type_id");
            var typeName = p.Value<string>("tag_type_name");
            var familyName = p.Value<string>("tag_family_name");
            if (!string.IsNullOrEmpty(familyName) && string.IsNullOrEmpty(typeName))
                return CommandResult.Fail("INVALID_PARAMS: tag_family_name needs tag_type_name.");
            if (typeId.HasValue || !string.IsNullOrEmpty(typeName))
            {
                if (!Lookup.TryFamilySymbol(doc, typeId, familyName, typeName, BuiltInCategory.OST_StructuralFramingTags, out tagType, out failure))
                    return failure;
                if (tagType.Category?.BuiltInCategory != BuiltInCategory.OST_StructuralFramingTags)
                    return CommandResult.Fail("WRONG_CATEGORY: type " + RevitCompat.GetId(tagType.Id) + " is not a structural framing tag type.");
            }
            else if (new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_StructuralFramingTags).FirstElement() == null)
            {
                return CommandResult.Fail("NO_FRAMING_TAG_TYPE: no structural framing tag family is loaded in the project.");
            }

            var skipped = new List<object>();
            List<Element> candidates;
            var ids = p["element_ids"] as JArray;
            if (ids != null && ids.Count > 0)
            {
                candidates = new List<Element>();
                foreach (var id in ids.Values<long>().Distinct())
                {
                    var element = doc.GetElement(RevitCompat.ToElementId(id));
                    if (element?.Category?.BuiltInCategory == BuiltInCategory.OST_StructuralFraming) candidates.Add(element);
                    else skipped.Add(new { element_id = id, reason = element == null ? "not found" : "not structural framing" });
                }
            }
            else
            {
                candidates = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_StructuralFraming)
                    .WhereElementIsNotElementType().ToList();
            }

            var alreadyTagged = new HashSet<long>(new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag))
                .Cast<IndependentTag>().SelectMany(t => t.GetTaggedLocalElementIds()).Select(RevitCompat.GetId));
            var todo = new List<Element>();
            foreach (var element in candidates)
            {
                if (alreadyTagged.Contains(RevitCompat.GetId(element.Id))) skipped.Add(new { element_id = RevitCompat.GetId(element.Id), reason = "already tagged" });
                else todo.Add(element);
            }
            if (todo.Count == 0)
                return CommandResult.Ok(new { view_id = RevitCompat.GetId(view.Id), tagged = 0, tag_ids = new long[0], skipped, failed = new object[0] });

            var tagIds = new List<long>();
            var failed = new List<object>();
            using (var tx = new Transaction(doc, "B2M: Tag Structural Framing"))
            {
                tx.Start();
                try
                {
                    if (tagType != null && !tagType.IsActive)
                    {
                        tagType.Activate();
                        doc.Regenerate();
                    }
                    foreach (var element in todo)
                    {
                        try
                        {
                            var point = TagPoint(element, view);
                            if (point == null) throw new InvalidOperationException("the member has no location.");
                            var tag = IndependentTag.Create(doc, view.Id, new Reference(element), false, TagMode.TM_ADDBY_CATEGORY,
                                TagOrientation.Horizontal, point);
                            if (tag == null) throw new InvalidOperationException("Revit returned no tag.");
                            if (tagType != null && tag.GetTypeId() != tagType.Id) tag.ChangeTypeId(tagType.Id);
                            tagIds.Add(RevitCompat.GetId(tag.Id));
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new { element_id = RevitCompat.GetId(element.Id), error = ex.Message });
                        }
                    }
                    var result = new { view_id = RevitCompat.GetId(view.Id), tagged = tagIds.Count, tag_ids = tagIds, skipped, failed };
                    if (tagIds.Count == 0)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("TAG_FAILED: no member could be tagged; see failed. Nothing was created.", result);
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                    return CommandResult.Ok(result);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("TAG_FAILED: " + ex.Message + " Nothing was created.");
                }
            }
        }

        private static XYZ TagPoint(Element element, View view)
        {
            if (element.Location is LocationCurve curve) return curve.Curve.Evaluate(0.5, true);
            if (element.Location is LocationPoint point) return point.Point;
            var box = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
            return box == null ? null : (box.Min + box.Max) * 0.5;
        }
    }
}
