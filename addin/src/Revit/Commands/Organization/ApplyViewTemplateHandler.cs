// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Views/ApplyViewTemplateHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: viewIds is required (rvt-mcp silently used the active view); every target is checked
// first and a refusal reports every view's problem at once (rvt-mcp stopped at the first); a miss lists
// the valid templates; views that already use the template are reported as unchanged instead of needing
// replaceExisting; at most 500 views per call; "B2M: ..." transaction with a commit-status check.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Organization
{
    using View = Autodesk.Revit.DB.View;

    public sealed class ApplyViewTemplateHandler : IRevitCommand
    {
        private const int MaxViews = 500;

        public string Name => "apply_view_template";
        public string Description =>
            "Apply a view template to views. mode \"assign\" (default) sets each view's View Template; replaceExisting: true is needed to "
            + "replace a different template already assigned. mode \"applyParameters\" copies the template's settings once without assigning it. "
            + "All views are checked first (exists, not a template, compatible type); if any fails nothing changes and every problem is listed.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""templateId"":{""type"":""integer""},
            ""viewIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""mode"":{""type"":""string"",""enum"":[""assign"",""applyParameters""]},
            ""replaceExisting"":{""type"":""boolean""}},
            ""required"":[""templateId"",""viewIds""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var ids = p["viewIds"].ToObject<long[]>();
            if (ids.Length == 0) return CommandResult.Fail("INVALID_PARAMS: viewIds must not be empty.");
            if (ids.Length > MaxViews) return CommandResult.Fail("INVALID_PARAMS: at most " + MaxViews + " views per call.");
            if (ids.Distinct().Count() != ids.Length) return CommandResult.Fail("INVALID_PARAMS: a view id is listed twice.");
            var assign = (p.Value<string>("mode") ?? "assign") == "assign";
            var replaceExisting = p.Value<bool?>("replaceExisting") ?? false;

            if (!OrganizationHelpers.TryTemplate(doc, p.Value<long>("templateId"), out var template, out var failure)) return failure;

            var views = new List<View>();
            var problems = new JArray();
            foreach (var id in ids)
            {
                var view = doc.GetElement(RevitCompat.ToElementId(id)) as View;
                string problem = null;
                if (view == null) problem = "VIEW_NOT_FOUND: no view has id " + id + ".";
                else if (view.IsTemplate) problem = "VIEW_IS_TEMPLATE: '" + view.Name + "' is a view template.";
                else if (!view.IsValidViewTemplate(template.Id))
                    problem = "INCOMPATIBLE: template '" + template.Name + "' (" + template.ViewType + ") cannot apply to '" + view.Name + "' (" + view.ViewType + ").";
                else if (assign && !replaceExisting && view.ViewTemplateId != ElementId.InvalidElementId && view.ViewTemplateId != template.Id)
                    problem = "TEMPLATE_ASSIGNED: '" + view.Name + "' already uses template '" + (doc.GetElement(view.ViewTemplateId) as View)?.Name
                        + "'; pass replaceExisting: true to replace it.";
                if (problem != null) problems.Add(new JObject { ["viewId"] = id, ["viewName"] = view?.Name, ["error"] = problem });
                else views.Add(view);
            }
            if (problems.Count > 0)
                return CommandResult.Fail("TARGETS_REJECTED: " + problems.Count + " of " + ids.Length + " view(s) cannot take template '"
                    + template.Name + "'. Nothing was changed.", new JObject
                {
                    ["problems"] = problems,
                    ["compatible_view_type"] = template.ViewType.ToString()
                });

            var results = new JArray();
            using (var tx = new Transaction(doc, "B2M: Apply view template"))
            {
                tx.Start();
                try
                {
                    foreach (var view in views)
                    {
                        var previousId = view.ViewTemplateId;
                        var previous = previousId == ElementId.InvalidElementId ? null : doc.GetElement(previousId) as View;
                        var unchanged = assign && previousId == template.Id;
                        if (!unchanged)
                        {
                            if (assign) view.ViewTemplateId = template.Id;
                            else view.ApplyViewTemplateParameters(template);
                        }
                        results.Add(new JObject
                        {
                            ["viewId"] = RevitCompat.GetId(view.Id),
                            ["viewName"] = view.Name,
                            ["viewType"] = view.ViewType.ToString(),
                            ["previousTemplateId"] = previous == null ? null : (JToken)RevitCompat.GetId(previous.Id),
                            ["previousTemplateName"] = previous?.Name,
                            ["status"] = unchanged ? "unchanged" : assign ? (previous == null ? "assigned" : "replaced") : "parameters_applied"
                        });
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures. No view was changed.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("APPLY_FAILED: could not apply the view template: " + ex.Message + " No view was changed.");
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["templateId"] = RevitCompat.GetId(template.Id),
                ["templateName"] = template.Name,
                ["mode"] = assign ? "assign" : "applyParameters",
                ["requested"] = ids.Length,
                ["changedCount"] = results.Count(r => r.Value<string>("status") != "unchanged"),
                ["unchangedCount"] = results.Count(r => r.Value<string>("status") == "unchanged"),
                ["results"] = results
            });
        }
    }
}
