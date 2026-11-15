// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Views/CreateViewTemplateFromViewHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: sourceViewId is required (rvt-mcp silently used the active view); setting IDs are
// checked against the new template's settings and a bad one lists the valid settings; duplicate setting
// IDs are refused; the template name is checked and trimmed before the transaction; the transaction is
// "B2M: ..." with a commit-status check.

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

    public sealed class CreateViewTemplateFromViewHandler : IRevitCommand
    {
        public string Name => "create_view_template_from_view";
        public string Description =>
            "Create a new view template from a view's current settings. sourceViewId is required. Optionally set exactly which settings "
            + "the template controls: controlledSettingIds OR nonControlledSettingIds (setting ids as listed by list_view_templates with includeSettings). "
            + "A name already used by a template fails unless failIfNameExists is false, which adds a \" (2)\" style suffix.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""templateName"":{""type"":""string""},
            ""sourceViewId"":{""type"":""integer""},
            ""controlledSettingIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""nonControlledSettingIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""failIfNameExists"":{""type"":""boolean""}},
            ""required"":[""templateName"",""sourceViewId""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var templateName = (p.Value<string>("templateName") ?? "").Trim();
            if (templateName.Length == 0) return CommandResult.Fail("INVALID_PARAMS: templateName must not be empty.");
            var controlled = p["controlledSettingIds"]?.ToObject<long[]>();
            var nonControlled = p["nonControlledSettingIds"]?.ToObject<long[]>();
            if (controlled != null && nonControlled != null)
                return CommandResult.Fail("INVALID_PARAMS: pass controlledSettingIds or nonControlledSettingIds, not both.");
            var requested = controlled ?? nonControlled;
            if (requested != null && requested.Distinct().Count() != requested.Length)
                return CommandResult.Fail("INVALID_PARAMS: a setting id is listed twice.");
            var failIfNameExists = p.Value<bool?>("failIfNameExists") ?? true;

            if (!Lookup.TryView(doc, p.Value<long>("sourceViewId"), out var source, out var failure)) return failure;

            var finalName = templateName;
            if (OrganizationHelpers.TemplateNameTaken(doc, finalName))
            {
                if (failIfNameExists)
                    return CommandResult.Fail("NAME_IN_USE: a view template named '" + finalName + "' already exists.");
                var suffix = 2;
                while (OrganizationHelpers.TemplateNameTaken(doc, templateName + " (" + suffix + ")")) suffix++;
                finalName = templateName + " (" + suffix + ")";
            }

            using (var tx = new Transaction(doc, "B2M: Create view template"))
            {
                tx.Start();
                try
                {
                    var template = source.CreateViewTemplate();
                    if (template == null)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("CREATE_FAILED: Revit could not make a template from view '" + source.Name + "' (" + source.ViewType + ").");
                    }
                    template.Name = finalName;

                    if (requested != null)
                    {
                        var all = template.GetTemplateParameterIds();
                        var allValues = new HashSet<long>(all.Select(RevitCompat.GetId));
                        var invalid = requested.Where(id => !allValues.Contains(id)).ToList();
                        if (invalid.Count > 0)
                        {
                            var validSettings = new JArray(all.Select(id => OrganizationHelpers.SettingInfo(doc, template, id)));
                            tx.RollBack();
                            return CommandResult.Fail("INVALID_SETTING: setting id(s) " + string.Join(", ", invalid)
                                + " are not settings of a " + source.ViewType + " template. Nothing was created.",
                                new JObject { ["valid_settings"] = validSettings });
                        }
                        var requestedSet = new HashSet<long>(requested);
                        var uncontrolled = controlled != null
                            ? all.Where(id => !requestedSet.Contains(RevitCompat.GetId(id))).ToList()
                            : all.Where(id => requestedSet.Contains(RevitCompat.GetId(id))).ToList();
                        template.SetNonControlledTemplateParameterIds(uncontrolled);
                    }

                    var controlledCount = OrganizationHelpers.ControlledCount(template);
                    var nonControlledCount = template.GetNonControlledTemplateParameterIds().Count;
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures. No template was created.");

                    return CommandResult.Ok(new
                    {
                        created = true,
                        templateId = RevitCompat.GetId(template.Id),
                        templateName = template.Name,
                        renamedBecauseNameExisted = finalName != templateName,
                        sourceViewId = RevitCompat.GetId(source.Id),
                        sourceViewName = source.Name,
                        viewType = template.ViewType.ToString(),
                        controlledSettingCount = controlledCount,
                        nonControlledSettingCount = nonControlledCount
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the view template: " + ex.Message);
                }
            }
        }
    }
}
