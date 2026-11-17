// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Parameters/GetTypeParametersHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: parameter rows share get_element_parameters' format (wire units, unit, internalValue); adds
// nameFilter; at most 200 IDs per list.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Query
{
    public sealed class GetTypeParametersHandler : IRevitCommand
    {
        private const int MaxIds = 200;

        public string Name => "get_type_parameters";
        public string Description => "Type parameters of element types given by typeIds and/or by the types of elementIds. "
            + "Values in mm / m2 / m3 / degrees; nameFilter keeps parameters whose name contains the text.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""typeIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""nameFilter"":{""type"":""string""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            var elementIds = p["elementIds"]?.ToObject<long[]>().Distinct().ToArray() ?? new long[0];
            var typeIds = p["typeIds"]?.ToObject<long[]>().Distinct().ToArray() ?? new long[0];
            if (elementIds.Length == 0 && typeIds.Length == 0) return CommandResult.Fail("INVALID_PARAMS: give elementIds or typeIds.");
            if (elementIds.Length > MaxIds || typeIds.Length > MaxIds) return CommandResult.Fail("INVALID_PARAMS: at most " + MaxIds + " IDs per list.");
            var nameFilter = p.Value<string>("nameFilter");

            var ordered = new List<long>();
            var sources = new Dictionary<long, List<long>>();
            var missingElementIds = new JArray();
            var elementsWithoutType = new JArray();
            foreach (var id in elementIds)
            {
                var element = doc.GetElement(RevitCompat.ToElementId(id));
                if (element == null)
                {
                    missingElementIds.Add(id);
                    continue;
                }
                if (!(QueryHelpers.IdOrNull(element.GetTypeId()) is long typeId))
                {
                    elementsWithoutType.Add(id);
                    continue;
                }
                if (!sources.ContainsKey(typeId))
                {
                    sources[typeId] = new List<long>();
                    ordered.Add(typeId);
                }
                sources[typeId].Add(id);
            }
            foreach (var id in typeIds.Where(id => !sources.ContainsKey(id))) ordered.Add(id);

            var types = new JArray();
            var missingTypeIds = new JArray();
            foreach (var typeId in ordered.Distinct())
            {
                if (!(doc.GetElement(RevitCompat.ToElementId(typeId)) is ElementType type))
                {
                    missingTypeIds.Add(typeId);
                    continue;
                }
                var parameters = QueryHelpers.Parameters(doc, type, true, out var errors);
                if (!string.IsNullOrEmpty(nameFilter))
                    parameters = new JArray(parameters.Where(t =>
                        (t.Value<string>("name") ?? "").IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0));
                var row = new JObject
                {
                    ["typeId"] = typeId,
                    ["typeName"] = QueryHelpers.NameOf(type),
                    ["familyName"] = type.FamilyName,
                    ["category"] = type.Category?.Name,
                    ["builtInCategory"] = Lookup.BuiltInName(type.Category),
                    ["sourceElementIds"] = new JArray(sources.TryGetValue(typeId, out var s) ? s : new List<long>()),
                    ["parameters"] = parameters
                };
                if (errors.Count > 0) row["parameterErrors"] = errors;
                types.Add(row);
            }
            return CommandResult.Ok(new JObject
            {
                ["count"] = types.Count,
                ["types"] = types,
                ["missingElementIds"] = missingElementIds,
                ["elementsWithoutType"] = elementsWithoutType,
                ["missingTypeIds"] = missingTypeIds
            });
        }
    }
}
