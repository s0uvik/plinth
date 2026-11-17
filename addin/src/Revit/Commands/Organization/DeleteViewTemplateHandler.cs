// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Views/DeleteViewTemplateHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: dryRun still defaults to true; a dry run on a template in use says what clearFromViews
// would do instead of only "wouldDelete: false"; a miss lists the valid templates; the result names the
// views that used the template and whether they were detached; snake_case max_used_by_views became
// maxUsedByViews to match the other fields; "Plinth: ..." transaction with a commit-status check.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Organization
{
    using View = Autodesk.Revit.DB.View;

    public sealed class DeleteViewTemplateHandler : IRevitCommand
    {
        public string Name => "delete_view_template";
        public string Description =>
            "Delete a view template. dryRun defaults to TRUE: pass dryRun: false to delete. A template used by views is refused unless "
            + "clearFromViews: true, which detaches it from those views first. The result lists the views that used it (up to maxUsedByViews, default 100).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""templateId"":{""type"":""integer""},
            ""dryRun"":{""type"":""boolean""},
            ""clearFromViews"":{""type"":""boolean""},
            ""maxUsedByViews"":{""type"":""integer""}},
            ""required"":[""templateId""]}";
        // Deletes when dryRun is false; the flag is per command, so it is a write.
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var dryRun = p.Value<bool?>("dryRun") ?? true;
            var clearFromViews = p.Value<bool?>("clearFromViews") ?? false;
            var maxUsedByViews = p.Value<int?>("maxUsedByViews") ?? 100;
            if (maxUsedByViews < 1 || maxUsedByViews > 1000)
                return CommandResult.Fail("INVALID_PARAMS: maxUsedByViews must be between 1 and 1000.");
            if (!OrganizationHelpers.TryTemplate(doc, p.Value<long>("templateId"), out var template, out var failure)) return failure;

            var templateId = template.Id;
            var templateName = template.Name;
            var users = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.ViewTemplateId == templateId)
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var usedBy = new JArray(users.Take(maxUsedByViews).Select(v => new JObject
            {
                ["viewId"] = RevitCompat.GetId(v.Id),
                ["name"] = v.Name,
                ["viewType"] = v.ViewType.ToString()
            }));
            var blocked = users.Count > 0 && !clearFromViews;

            if (blocked && !dryRun)
                return CommandResult.Fail("TEMPLATE_IN_USE: template '" + templateName + "' is used by " + users.Count
                    + " view(s); pass clearFromViews: true to detach it from them and delete. Nothing was changed.",
                    new JObject { ["usedByViewCount"] = users.Count, ["usedByViews"] = usedBy });

            var deletedIds = new List<long>();
            if (!dryRun)
            {
                using (var tx = new Transaction(doc, "Plinth: Delete view template"))
                {
                    tx.Start();
                    try
                    {
                        foreach (var view in users) view.ViewTemplateId = ElementId.InvalidElementId;
                        deletedIds.AddRange(doc.Delete(templateId).Select(RevitCompat.GetId));
                        if (tx.Commit() != TransactionStatus.Committed)
                            return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures. Nothing was deleted.");
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                        return CommandResult.Fail("DELETE_FAILED: could not delete the view template: " + ex.Message + " Nothing was deleted.");
                    }
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["dryRun"] = dryRun,
                ["wouldDelete"] = !blocked,
                ["deleted"] = !dryRun,
                ["blockedReason"] = blocked ? "in use by " + users.Count + " view(s); pass clearFromViews: true" : null,
                ["templateId"] = RevitCompat.GetId(templateId),
                ["templateName"] = templateName,
                ["usedByViewCount"] = users.Count,
                ["usedByViews"] = usedBy,
                ["usedByViewsTruncated"] = users.Count > maxUsedByViews,
                ["viewsDetached"] = !dryRun ? users.Count : 0,
                ["deletedElementIds"] = new JArray(deletedIds.Take(maxUsedByViews)),
                ["deletedElementIdsTruncated"] = deletedIds.Count > maxUsedByViews
            });
        }
    }
}
