// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Views/ListViewTemplatesHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: includeSettings now defaults to false (setting lists are long; counts are always given)
// to stay under the response cap; an unknown viewType fails with the valid ViewType names instead of
// matching nothing; viewId must be a non-template view (Lookup.TryView); setting names come from
// OrganizationHelpers.SettingInfo; the read errors rvt-mcp swallowed are reported per template.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Organization
{
    using View = Autodesk.Revit.DB.View;

    public sealed class ListViewTemplatesHandler : IRevitCommand
    {
        public string Name => "list_view_templates";
        public string Description =>
            "List view templates (id, name, view type, controlled-setting counts), sorted by name. viewType filters by ViewType "
            + "(FloorPlan, CeilingPlan, ThreeD, Section, Elevation, DraftingView, ...). viewId reports isCompatibleWithTarget for that view. "
            + "includeSettings lists the controlled and uncontrolled settings; includeUsage lists the views using each template. limit 1-2000 (default 500).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""viewType"":{""type"":""string""},
            ""viewId"":{""type"":""integer""},
            ""includeSettings"":{""type"":""boolean""},
            ""includeUsage"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var viewTypeName = p.Value<string>("viewType");
            var includeSettings = p.Value<bool?>("includeSettings") ?? false;
            var includeUsage = p.Value<bool?>("includeUsage") ?? false;
            var limit = p.Value<int?>("limit") ?? 500;
            if (limit < 1 || limit > 2000) return CommandResult.Fail("INVALID_PARAMS: limit must be between 1 and 2000.");

            ViewType? viewType = null;
            if (!string.IsNullOrWhiteSpace(viewTypeName))
            {
                if (!Enum.TryParse(viewTypeName.Trim(), true, out ViewType parsed) || !Enum.IsDefined(typeof(ViewType), parsed))
                    return CommandResult.Fail("INVALID_PARAMS: '" + viewTypeName + "' is not a ViewType.",
                        new JObject { ["valid_view_types"] = new JArray(Enum.GetNames(typeof(ViewType))) });
                viewType = parsed;
            }

            View target = null;
            if (p["viewId"] != null && p["viewId"].Type != JTokenType.Null)
                if (!Lookup.TryView(doc, p.Value<long>("viewId"), out target, out var failure)) return failure;

            var templates = OrganizationHelpers.Templates(doc);
            var matched = templates.Where(t => viewType == null || t.ViewType == viewType.Value).ToList();
            var users = includeUsage
                ? new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate && v.ViewTemplateId != ElementId.InvalidElementId)
                    .ToLookup(v => RevitCompat.GetId(v.ViewTemplateId))
                : null;

            var rows = new JArray();
            foreach (var template in matched.Take(limit))
            {
                var row = new JObject
                {
                    ["templateId"] = RevitCompat.GetId(template.Id),
                    ["name"] = template.Name,
                    ["viewType"] = template.ViewType.ToString()
                };
                if (target != null) row["isCompatibleWithTarget"] = target.IsValidViewTemplate(template.Id);
                try
                {
                    var all = template.GetTemplateParameterIds();
                    var nonControlled = template.GetNonControlledTemplateParameterIds();
                    var nonSet = new System.Collections.Generic.HashSet<long>(nonControlled.Select(RevitCompat.GetId));
                    row["controlledSettingCount"] = all.Count - nonControlled.Count;
                    row["nonControlledSettingCount"] = nonControlled.Count;
                    if (includeSettings)
                    {
                        row["controlledSettings"] = new JArray(all.Where(id => !nonSet.Contains(RevitCompat.GetId(id)))
                            .Select(id => OrganizationHelpers.SettingInfo(doc, template, id))
                            .OrderBy(s => s.Value<string>("name") ?? "", StringComparer.OrdinalIgnoreCase));
                        row["nonControlledSettings"] = new JArray(nonControlled
                            .Select(id => OrganizationHelpers.SettingInfo(doc, template, id))
                            .OrderBy(s => s.Value<string>("name") ?? "", StringComparer.OrdinalIgnoreCase));
                    }
                }
                catch (Exception ex)
                {
                    row["settingsError"] = ex.Message;
                }
                if (users != null)
                {
                    var applied = users[RevitCompat.GetId(template.Id)].OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
                    row["appliedToViewCount"] = applied.Count;
                    row["appliedToViews"] = new JArray(applied.Select(v => new JObject
                    {
                        ["viewId"] = RevitCompat.GetId(v.Id),
                        ["name"] = v.Name,
                        ["viewType"] = v.ViewType.ToString()
                    }));
                }
                rows.Add(row);
            }

            return CommandResult.Ok(new JObject
            {
                ["totalTemplates"] = templates.Count,
                ["matched"] = matched.Count,
                ["returned"] = rows.Count,
                ["truncated"] = matched.Count > rows.Count,
                ["targetViewId"] = target == null ? null : (JToken)RevitCompat.GetId(target.Id),
                ["targetViewName"] = target?.Name,
                ["templates"] = rows
            });
        }
    }
}
