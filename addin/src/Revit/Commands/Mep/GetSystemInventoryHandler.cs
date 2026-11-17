// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Mep/GetSystemInventoryHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: the system lookup lists the valid systems on a miss or ambiguity; limit outside 1..20000 is an
// error (rvt-mcp clamped it); parameter values convert lengths through Mm and other units through Revit's own
// formatting (AsValueString) instead of hand-written factors; IDs are numbers; errors are "CODE: message".

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Mep
{
    public sealed class GetSystemInventoryHandler : IRevitCommand
    {
        private const int MaxParametersPerElement = 20;

        public string Name => "get_system_inventory";
        public string Description =>
            "List the members of one MEP system (by system_id or system_name): network elements, terminals and base equipment, "
            + "deduplicated, each with category and type, plus a count per category. limit caps the elements listed (default 2000, max 20000). "
            + "include_parameters adds up to 20 parameter values per element (lengths in mm).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""system_id"":{""type"":""integer""},
            ""system_name"":{""type"":""string""},
            ""include_parameters"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var includeParameters = p.Value<bool?>("include_parameters") ?? false;
            var limit = p.Value<int?>("limit") ?? 2000;
            if (limit < 1 || limit > 20000) return CommandResult.Fail("INVALID_PARAMS: limit must be between 1 and 20000.");
            if (!MepHelpers.TrySystem(doc, p.Value<long?>("system_id"), p.Value<string>("system_name"), out var system, out var failure))
                return failure;

            Element[] members;
            try
            {
                members = MepSystemMembership.Read(system).AllElements;
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("READ_FAILED: " + ex.Message);
            }
            members = members.Where(m => RevitCompat.GetId(m.Id) > 0).ToArray();

            var breakdown = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var rows = new JArray();
            foreach (var element in members)
            {
                var category = element.Category?.Name ?? "<none>";
                breakdown[category] = breakdown.TryGetValue(category, out var n) ? n + 1 : 1;
                if (rows.Count >= limit) continue;

                var row = new JObject
                {
                    ["id"] = RevitCompat.GetId(element.Id),
                    ["category"] = category,
                    ["built_in_category"] = Lookup.BuiltInName(element.Category),
                    ["type_name"] = MepHelpers.TypeName(doc, element),
                    ["name"] = MepHelpers.SafeName(element)
                };
                if (includeParameters) row["parameters"] = ReadParameters(doc, element);
                rows.Add(row);
            }

            return CommandResult.Ok(new
            {
                system_id = RevitCompat.GetId(system.Id),
                system_name = MepHelpers.SafeName(system) ?? "",
                domain = MepHelpers.DomainOf(system),
                system_type = MepHelpers.SystemTypeName(doc, system),
                total_elements = members.Length,
                returned = rows.Count,
                truncated = members.Length > rows.Count,
                category_breakdown = breakdown,
                elements = rows
            });
        }

        private static JObject ReadParameters(Document doc, Element element)
        {
            var map = new JObject();
            foreach (Parameter parameter in element.Parameters)
            {
                if (map.Count >= MaxParametersPerElement) break;
                try
                {
                    if (parameter?.Definition == null || !parameter.HasValue) continue;
                    var name = parameter.Definition.Name;
                    if (string.IsNullOrEmpty(name) || map[name] != null) continue;
                    var value = ValueOf(doc, parameter);
                    if (value != null) map[name] = value;
                }
                catch
                {
                    // One unreadable parameter must not hide the rest.
                }
            }
            return map;
        }

        private static JToken ValueOf(Document doc, Parameter parameter)
        {
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return parameter.AsString();
                case StorageType.Integer:
                    return parameter.AsInteger();
                case StorageType.Double:
                    var spec = parameter.Definition.GetDataType();
                    if (spec == SpecTypeId.Length || spec == SpecTypeId.PipeSize || spec == SpecTypeId.DuctSize
                        || spec == SpecTypeId.CableTraySize || spec == SpecTypeId.ConduitSize)
                        return Mm.FromFeet(parameter.AsDouble());
                    return parameter.AsValueString() ?? (JToken)parameter.AsDouble();
                case StorageType.ElementId:
                    var id = parameter.AsElementId();
                    return MepHelpers.SafeName(doc.GetElement(id)) ?? (JToken)RevitCompat.GetId(id);
                default:
                    return null;
            }
        }
    }
}
