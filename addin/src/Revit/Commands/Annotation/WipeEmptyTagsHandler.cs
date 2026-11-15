// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Annotations/WipeEmptyTagsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: tagged ids come from GetTaggedLocalElementIds (no reflection); when tags were due for deletion
// and none could be deleted the transaction is rolled back and the command fails; limit must be 1 to 500.
// dry_run still defaults to true. ReadOnly is false because a call with dry_run=false deletes tags.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Annotation
{
    // B2M.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class WipeEmptyTagsHandler : IRevitCommand
    {
        public string Name => "wipe_empty_tags";
        public string Description =>
            "Find tags owned by a view (view_id, default the active view) whose text is empty and delete them. "
            + "dry_run defaults to TRUE: pass dry_run=false to delete. limit (default 200, max 500) caps how many are deleted.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""view_id"":{""type"":""integer""},
            ""dry_run"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!AnnotationHelpers.TryLimit(p, 200, 500, out var limit, out var failure)) return failure;
            if (!AnnotationHelpers.TryAnnotationView(doc, p.Value<long?>("view_id"), out View view, out failure)) return failure;
            var dryRun = p.Value<bool?>("dry_run") ?? true;

            // Ownership includes hidden or cropped tags, which a visibility collector would miss.
            var scanned = new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).OwnedByView(view.Id)
                .Cast<IndependentTag>().ToList();
            var empty = new List<IndependentTag>();
            var failed = new JArray();
            foreach (var tag in scanned)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(tag.TagText)) empty.Add(tag);
                }
                catch (Exception ex)
                {
                    failed.Add(new JObject { ["tag_id"] = RevitCompat.GetId(tag.Id), ["error"] = "could not read the tag text: " + ex.Message });
                }
            }
            var truncated = empty.Count > limit;
            var targets = empty.Take(limit).ToList();

            var result = new JObject
            {
                ["dry_run"] = dryRun,
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["view_name"] = view.Name,
                ["scanned"] = scanned.Count,
                ["empty_count"] = empty.Count,
                ["truncated"] = truncated,
                ["would_delete"] = new JArray(targets.Select(t => new JObject
                {
                    ["tag_id"] = RevitCompat.GetId(t.Id),
                    ["tagged_element_ids"] = new JArray(AnnotationHelpers.TaggedIds(t))
                }))
            };
            if (dryRun || targets.Count == 0)
            {
                result["deleted"] = 0;
                result["failed"] = failed;
                return CommandResult.Ok(result);
            }

            var deleted = new JArray();
            using (var tx = new Transaction(doc, "B2M: Wipe Empty Tags"))
            {
                tx.Start();
                try
                {
                    foreach (var tag in targets)
                    {
                        var id = RevitCompat.GetId(tag.Id);
                        try
                        {
                            doc.Delete(tag.Id);
                            deleted.Add(id);
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new JObject { ["tag_id"] = id, ["error"] = ex.Message });
                        }
                    }
                    result["failed"] = failed;
                    if (deleted.Count == 0)
                    {
                        tx.RollBack();
                        result["deleted"] = 0;
                        return CommandResult.Fail("DELETE_FAILED: none of the empty tags could be deleted; see failed. Nothing was changed.", result);
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("DELETE_FAILED: could not delete the empty tags: " + ex.Message + " Nothing was changed.");
                }
            }

            result["deleted"] = deleted.Count;
            result["deleted_ids"] = deleted;
            return CommandResult.Ok(result);
        }
    }
}
