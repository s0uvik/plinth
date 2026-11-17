// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Parameters/ListProjectParameterBindingsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: rows are ordered without "dynamic"; limit outside 1-5000 is an error; the parameter element is
// taken from the binding's InternalDefinition only (rvt-mcp fell back to a name match); categories carry
// builtInCategory; family documents are refused.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Parameters
{
    public sealed class ListProjectParameterBindingsHandler : IRevitCommand
    {
        public string Name => "list_project_parameter_bindings";
        public string Description =>
            "List the project's parameter bindings (project and shared parameters): name, GUID if shared, instance/type, group, data type and categories. "
            + "Filter by nameFilter (contains) or guid.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""includeCategories"":{""type"":""boolean""},
            ""includeShared"":{""type"":""boolean""},
            ""includeProject"":{""type"":""boolean""},
            ""nameFilter"":{""type"":""string""},
            ""guid"":{""type"":""string""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!ParametersHelpers.RequireProject(doc, out var failure)) return failure;
            var withCategories = p.Value<bool?>("includeCategories") ?? true;
            var shared = p.Value<bool?>("includeShared") ?? true;
            var project = p.Value<bool?>("includeProject") ?? true;
            var nameFilter = p.Value<string>("nameFilter");
            var limit = p.Value<int?>("limit") ?? 1000;
            if (!shared && !project) return CommandResult.Fail("INVALID_PARAMS: includeShared and includeProject cannot both be false.");
            if (limit < 1 || limit > 5000) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to 5000.");
            Guid? guidFilter = null;
            if (!string.IsNullOrEmpty(p.Value<string>("guid")))
            {
                if (!ParametersHelpers.TryGuid(p.Value<string>("guid"), out var g, out failure)) return failure;
                guidFilter = g;
            }

            var rows = ParametersHelpers.Bindings(doc).Select(kv =>
                {
                    var guid = ParametersHelpers.SharedGuidOf(doc, kv.Key);
                    return new { Def = kv.Key, Binding = kv.Value, Guid = guid };
                })
                .Where(r => r.Guid.HasValue ? shared : project)
                .Where(r => string.IsNullOrEmpty(nameFilter) || r.Def.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(r => !guidFilter.HasValue || r.Guid == guidFilter)
                .OrderBy(r => r.Def.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return CommandResult.Ok(new JObject
            {
                ["count"] = rows.Count,
                ["returned"] = Math.Min(limit, rows.Count),
                ["bindings"] = new JArray(rows.Take(limit).Select(r =>
                {
                    ForgeTypeId group = null, spec = null;
                    try { group = r.Def.GetGroupTypeId(); } catch { }
                    try { spec = r.Def.GetDataType(); } catch { }
                    var o = new JObject
                    {
                        ["name"] = r.Def.Name,
                        ["parameterElementId"] = r.Def is InternalDefinition d ? (JToken)RevitCompat.GetId(d.Id) : null,
                        ["isShared"] = r.Guid.HasValue,
                        ["guid"] = r.Guid?.ToString("d"),
                        ["bindingKind"] = ParametersHelpers.KindOf(r.Binding),
                        ["parameterGroupId"] = group?.TypeId,
                        ["groupLabel"] = ParametersHelpers.GroupLabel(group),
                        ["dataTypeId"] = spec?.TypeId,
                        ["dataTypeLabel"] = ParametersHelpers.SpecLabel(spec),
                        ["categoryCount"] = r.Binding.Categories?.Size ?? 0
                    };
                    if (withCategories) o["categories"] = ParametersHelpers.CategoriesJson(r.Binding);
                    return o;
                }))
            });
        }
    }
}
