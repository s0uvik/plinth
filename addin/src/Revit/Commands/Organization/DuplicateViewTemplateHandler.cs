// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Views/DuplicateViewTemplateHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: a miss lists the valid templates; the copy keeps the source's controlled/uncontrolled
// split explicitly (CreateViewTemplate on a template does not promise to); "B2M: ..." transaction with a
// commit-status check.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Organization
{
    public sealed class DuplicateViewTemplateHandler : IRevitCommand
    {
        public string Name => "duplicate_view_template";
        public string Description => "Copy a view template under a new name (unique among templates). Returns the new template's id.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""templateId"":{""type"":""integer""},
            ""newName"":{""type"":""string""}},
            ""required"":[""templateId"",""newName""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var newName = (p.Value<string>("newName") ?? "").Trim();
            if (newName.Length == 0) return CommandResult.Fail("INVALID_PARAMS: newName must not be empty.");
            if (!OrganizationHelpers.TryTemplate(doc, p.Value<long>("templateId"), out var source, out var failure)) return failure;
            if (OrganizationHelpers.TemplateNameTaken(doc, newName))
                return CommandResult.Fail("NAME_IN_USE: a view template named '" + newName + "' already exists.");

            using (var tx = new Transaction(doc, "B2M: Duplicate view template"))
            {
                tx.Start();
                try
                {
                    // View.Duplicate refuses templates; CreateViewTemplate on a template makes the supported copy.
                    var copy = source.CreateViewTemplate();
                    if (copy == null)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("DUPLICATE_FAILED: Revit could not copy template '" + source.Name + "'.");
                    }
                    copy.Name = newName;
                    var sourceUncontrolled = source.GetNonControlledTemplateParameterIds();
                    var copySettings = copy.GetTemplateParameterIds().Select(RevitCompat.GetId).ToList();
                    copy.SetNonControlledTemplateParameterIds(sourceUncontrolled
                        .Where(id => copySettings.Contains(RevitCompat.GetId(id))).ToList());

                    var controlledCount = OrganizationHelpers.ControlledCount(copy);
                    var nonControlledCount = copy.GetNonControlledTemplateParameterIds().Count;
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures. No template was created.");

                    return CommandResult.Ok(new
                    {
                        duplicated = true,
                        sourceTemplateId = RevitCompat.GetId(source.Id),
                        sourceTemplateName = source.Name,
                        templateId = RevitCompat.GetId(copy.Id),
                        templateName = copy.Name,
                        viewType = copy.ViewType.ToString(),
                        controlledSettingCount = controlledCount,
                        nonControlledSettingCount = nonControlledCount
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("DUPLICATE_FAILED: could not duplicate the view template: " + ex.Message);
                }
            }
        }
    }
}
