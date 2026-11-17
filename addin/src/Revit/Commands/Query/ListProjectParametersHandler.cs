// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Parameters/ListProjectParametersHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: the shared-parameter GUID now comes from the SharedParameterElement (rvt-mcp cast the binding's
// definition to ExternalDefinition, which bindings never are, so guid was always null); categories add builtInCategory.

using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Query
{
    public sealed class ListProjectParametersHandler : IRevitCommand
    {
        public string Name => "list_project_parameters";
        public string Description => "List the project's parameter bindings: name, group, data type, instance or type, shared GUID and bound categories.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""includeCategories"":{""type"":""boolean""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            var includeCategories = p.Value<bool?>("includeCategories") ?? true;

            var rows = new List<JObject>();
            var iterator = doc.ParameterBindings.ForwardIterator();
            iterator.Reset();
            while (iterator.MoveNext())
            {
                var definition = iterator.Key;
                if (definition == null || !(iterator.Current is ElementBinding binding)) continue;
                var shared = definition is InternalDefinition internalDefinition
                    ? doc.GetElement(internalDefinition.Id) as SharedParameterElement
                    : null;
                var row = new JObject
                {
                    ["name"] = definition.Name,
                    ["parameterGroup"] = TypeId(() => definition.GetGroupTypeId()),
                    ["groupLabel"] = Label(() => LabelUtils.GetLabelForGroup(definition.GetGroupTypeId())),
                    ["specId"] = TypeId(() => definition.GetDataType()),
                    ["dataType"] = Label(() => LabelUtils.GetLabelForSpec(definition.GetDataType())),
                    ["bindingKind"] = binding is InstanceBinding ? "instance" : binding is TypeBinding ? "type" : binding.GetType().Name,
                    ["isShared"] = shared != null,
                    ["guid"] = shared?.GuidValue.ToString()
                };
                if (includeCategories)
                    row["categories"] = new JArray(binding.Categories.Cast<Category>().OrderBy(c => c.Name).Select(c => new JObject
                    {
                        ["name"] = c.Name,
                        ["id"] = RevitCompat.GetId(c.Id),
                        ["builtInCategory"] = Lookup.BuiltInName(c)
                    }));
                rows.Add(row);
            }
            return CommandResult.Ok(new JObject
            {
                ["count"] = rows.Count,
                ["includeCategories"] = includeCategories,
                ["bindings"] = new JArray(rows.OrderBy(r => r.Value<string>("name")))
            });
        }

        private static string TypeId(System.Func<ForgeTypeId> get)
        {
            try
            {
                var id = get()?.TypeId;
                return string.IsNullOrEmpty(id) ? null : id;
            }
            catch
            {
                return null;
            }
        }

        private static string Label(System.Func<string> get)
        {
            try
            {
                return get();
            }
            catch
            {
                return null;
            }
        }
    }
}
