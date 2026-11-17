// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Families/ListFamilyTypesInFamilyHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: values use the shared read rules (lengths through Mm, areas m2, volumes m3, angles degrees,
// other measured values marked "internal"); element parameters return the id plus the element's name instead of
// the name in place of the id; family_id is an integer; misses list the valid families; no reflection fallbacks.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Plinth.Revit.Commands.Parameters;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Families
{
    public sealed class ListFamilyTypesInFamilyHandler : IRevitCommand
    {
        public string Name => "list_family_types_in_family";
        public string Description =>
            "List the types of one family (family_id or family_name) with their type parameter values in mm / m2 / m3 / degrees. "
            + "parameter_names limits the parameters; include_built_in_only skips shared and project parameters. Paged by start_index / max_types.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + FamiliesHelpers.FamilySchema + @",
            ""include_parameter_values"":{""type"":""boolean""},
            ""include_built_in_only"":{""type"":""boolean""},
            ""start_index"":{""type"":""integer""},
            ""max_types"":{""type"":""integer""},
            ""parameter_names"":{""type"":""array"",""items"":{""type"":""string""}}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!FamiliesHelpers.TryFamily(doc, p.Value<long?>("family_id"), p.Value<string>("family_name"), out var family, out var failure))
                return failure;

            var withValues = p.Value<bool?>("include_parameter_values") ?? true;
            var builtInOnly = p.Value<bool?>("include_built_in_only") ?? false;
            var start = p.Value<int?>("start_index") ?? 0;
            var size = p.Value<int?>("max_types") ?? 100;
            if (start < 0) return CommandResult.Fail("INVALID_PARAMS: start_index must not be negative.");
            if (size < 1 || size > 500) return CommandResult.Fail("INVALID_PARAMS: max_types must be 1 to 500.");
            var names = new HashSet<string>(p["parameter_names"]?.Values<string>() ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            var symbols = family.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>()
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var meta = FamiliesHelpers.Page(symbols, start, size, s =>
            {
                var o = new JObject
                {
                    ["id"] = RevitCompat.GetId(s.Id),
                    ["name"] = s.Name,
                    ["is_active"] = s.IsActive
                };
                if (withValues) o["parameters"] = Values(doc, s, builtInOnly, names);
                return o;
            }, out var page);

            var result = new JObject
            {
                ["family_id"] = RevitCompat.GetId(family.Id),
                ["family_name"] = family.Name,
                ["category"] = family.FamilyCategory?.Name,
                ["builtInCategory"] = Lookup.BuiltInName(family.FamilyCategory),
                ["kind"] = FamiliesHelpers.Kind(family),
                ["is_editable"] = family.IsEditable,
                ["total_type_count"] = meta["total"]
            };
            foreach (var prop in meta.Properties().Where(x => x.Name != "total")) result[prop.Name] = prop.Value;
            result["types"] = page;
            return CommandResult.Ok(result);
        }

        private static JObject Values(Document doc, FamilySymbol symbol, bool builtInOnly, HashSet<string> names)
        {
            var o = new JObject();
            foreach (Parameter parameter in symbol.Parameters)
            {
                var name = parameter?.Definition?.Name;
                if (string.IsNullOrEmpty(name) || o[name] != null) continue;
                if (names.Count > 0 && !names.Contains(name)) continue;
                var builtIn = parameter.Definition is InternalDefinition d && d.BuiltInParameter != BuiltInParameter.INVALID;
                if (builtInOnly && !builtIn) continue;
                o[name] = ParametersHelpers.Read(doc, parameter);
            }
            return o;
        }
    }
}
