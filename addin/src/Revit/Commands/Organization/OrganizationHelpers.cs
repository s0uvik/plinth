// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), files
// src/shared/Handlers/Views/ListViewTemplatesHandler.cs (ResolveParameterInfo) and the template / selection
// lookups repeated in src/shared/Handlers/Views/*ViewTemplate*.cs and src/shared/Handlers/Organization/*Selection*.cs,
// commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: one shared lookup per kind; a miss returns the valid options (rvt-mcp returned a bare
// "not found"); template setting names come from ParameterElement / BuiltInParameter labels instead of a
// scan of the template's parameters.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Organization
{
    using View = Autodesk.Revit.DB.View;

    internal static class OrganizationHelpers
    {
        public const int MaxOptions = 100;

        /// <summary>Most element IDs one call may select or save.</summary>
        public const int MaxElementIds = 5000;

        public static List<View> Templates(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => v.IsTemplate).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();

        public static JArray TemplateOptions(IEnumerable<View> templates) =>
            new JArray(templates.Take(MaxOptions).Select(t => new JObject
            {
                ["id"] = RevitCompat.GetId(t.Id),
                ["name"] = t.Name,
                ["view_type"] = t.ViewType.ToString()
            }));

        /// <summary>A view template by element ID; a miss lists the valid templates.</summary>
        public static bool TryTemplate(Document doc, long id, out View template, out CommandResult failure)
        {
            failure = null;
            template = doc.GetElement(RevitCompat.ToElementId(id)) as View;
            if (template != null && template.IsTemplate) return true;
            var message = template == null
                ? "TEMPLATE_NOT_FOUND: no view template has id " + id + "."
                : "NOT_A_TEMPLATE: element " + id + " is the view '" + template.Name + "', not a view template.";
            template = null;
            failure = CommandResult.Fail(message, new JObject { ["valid_templates"] = TemplateOptions(Templates(doc)) });
            return false;
        }

        public static bool TemplateNameTaken(Document doc, string name) =>
            Templates(doc).Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

        public static List<SelectionFilterElement> Selections(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(SelectionFilterElement)).Cast<SelectionFilterElement>()
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();

        public static JArray SelectionOptions(IEnumerable<SelectionFilterElement> selections) =>
            new JArray(selections.Take(MaxOptions).Select(f => new JObject
            {
                ["id"] = RevitCompat.GetId(f.Id),
                ["name"] = f.Name
            }));

        /// <summary>
        /// A saved selection (SelectionFilterElement) by ID or by exact name (case-insensitive), exactly one of them.
        /// A miss or an ambiguous name lists the valid selections.
        /// </summary>
        public static bool TrySelection(Document doc, long? id, string name, string idField, string nameField,
            out SelectionFilterElement selection, out CommandResult failure)
        {
            selection = null;
            failure = null;
            var hasName = !string.IsNullOrWhiteSpace(name);
            if (id.HasValue == hasName)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: give exactly one of " + idField + " or " + nameField + ".");
                return false;
            }
            var all = Selections(doc);
            string message;
            if (id.HasValue)
            {
                selection = doc.GetElement(RevitCompat.ToElementId(id.Value)) as SelectionFilterElement;
                if (selection != null) return true;
                message = "SELECTION_NOT_FOUND: no saved selection has id " + id.Value + ".";
            }
            else
            {
                var matches = all.Where(f => string.Equals(f.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 1)
                {
                    selection = matches[0];
                    return true;
                }
                message = matches.Count == 0
                    ? "SELECTION_NOT_FOUND: no saved selection named '" + name.Trim() + "'."
                    : "SELECTION_AMBIGUOUS: several saved selections are named '" + name.Trim() + "'; pass " + idField + ".";
                if (matches.Count > 1) all = matches;
            }
            failure = CommandResult.Fail(message, new JObject { ["valid_selections"] = SelectionOptions(all) });
            return false;
        }

        /// <summary>Splits IDs into elements that exist and IDs that do not.</summary>
        public static void Resolve(Document doc, IEnumerable<ElementId> ids, out List<ElementId> found, out List<long> missing)
        {
            found = new List<ElementId>();
            missing = new List<long>();
            foreach (var id in ids)
            {
                if (doc.GetElement(id) != null) found.Add(id);
                else missing.Add(RevitCompat.GetId(id));
            }
        }

        public static JObject ElementSummary(Document doc, Element element) => new JObject
        {
            ["elementId"] = RevitCompat.GetId(element.Id),
            ["name"] = element.Name,
            ["category"] = element.Category?.Name,
            ["builtInCategory"] = Lookup.BuiltInName(element.Category),
            ["typeName"] = doc.GetElement(element.GetTypeId())?.Name
        };

        /// <summary>A template setting (a parameter ID from GetTemplateParameterIds) with a readable name.</summary>
        public static JObject SettingInfo(Document doc, View template, ElementId parameterId)
        {
            var value = RevitCompat.GetId(parameterId);
            string name = null;
            string builtIn = null;
            try
            {
                if (value < 0 && value >= int.MinValue)
                {
                    var bip = (BuiltInParameter)(int)value;
                    if (Enum.IsDefined(typeof(BuiltInParameter), bip))
                    {
                        builtIn = bip.ToString();
                        name = template.get_Parameter(bip)?.Definition?.Name ?? LabelUtils.GetLabelFor(bip);
                    }
                }
                else
                {
                    name = (doc.GetElement(parameterId) as ParameterElement)?.GetDefinition()?.Name;
                }
            }
            catch (Exception)
            {
                // A setting without a readable label is still listed by id.
            }
            return new JObject { ["id"] = value, ["name"] = name, ["builtInParameter"] = builtIn };
        }

        public static int ControlledCount(View template) =>
            template.GetTemplateParameterIds().Count - template.GetNonControlledTemplateParameterIds().Count;
    }
}
