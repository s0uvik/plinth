// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Parameters/GetElementParametersHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: "value" is in wire units (length mm, area m2, volume m3, angle degrees) with "unit", and the raw
// internal double is kept as internalValue; adds isShared/guid; parameters are sorted by name; nameFilter narrows the
// list; at most 200 elements per call.

using System;
using System.Linq;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Query
{
    public sealed class GetElementParametersHandler : IRevitCommand
    {
        private const int MaxIds = 200;

        public string Name => "get_element_parameters";
        public string Description => "Instance parameters of elements by ID, with values in mm / m2 / m3 / degrees and Revit's display text. "
            + "includeReadOnly (default true); nameFilter keeps parameters whose name contains the text.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""includeReadOnly"":{""type"":""boolean""},
            ""nameFilter"":{""type"":""string""}},""required"":[""elementIds""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            var ids = p["elementIds"].ToObject<long[]>().Distinct().ToArray();
            if (ids.Length == 0 || ids.Length > MaxIds) return CommandResult.Fail("INVALID_PARAMS: give 1 to " + MaxIds + " elementIds.");
            var includeReadOnly = p.Value<bool?>("includeReadOnly") ?? true;
            var nameFilter = p.Value<string>("nameFilter");

            var elements = new JArray();
            var missing = 0;
            foreach (var id in ids)
            {
                var element = doc.GetElement(RevitCompat.ToElementId(id));
                if (element == null)
                {
                    missing++;
                    elements.Add(new JObject { ["elementId"] = id, ["found"] = false, ["error"] = "not found" });
                    continue;
                }
                var parameters = QueryHelpers.Parameters(doc, element, includeReadOnly, out var errors);
                if (!string.IsNullOrEmpty(nameFilter))
                    parameters = new JArray(parameters.Where(t =>
                        (t.Value<string>("name") ?? "").IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0));
                var row = new JObject
                {
                    ["elementId"] = id,
                    ["found"] = true,
                    ["name"] = QueryHelpers.NameOf(element),
                    ["category"] = element.Category?.Name,
                    ["typeId"] = QueryHelpers.Id(element.GetTypeId()),
                    ["parameterCount"] = parameters.Count,
                    ["parameters"] = parameters
                };
                if (errors.Count > 0) row["parameterErrors"] = errors;
                elements.Add(row);
            }
            return CommandResult.Ok(new JObject
            {
                ["requested"] = ids.Length,
                ["found"] = ids.Length - missing,
                ["missing"] = missing,
                ["includeReadOnly"] = includeReadOnly,
                ["elements"] = elements
            });
        }
    }
}
