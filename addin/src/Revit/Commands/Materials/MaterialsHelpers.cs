// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), the material lookup and color checks
// repeated in src/shared/Handlers/Materials/*.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: one lookup whose misses carry the valid materials; passing both id and name is refused;
// colour is one {red, green, blue} object; property-set names are checked before Revit throws on a duplicate.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Materials
{
    internal static class MaterialsHelpers
    {
        private const int MaxOptions = 100;

        public const string MaterialSchema = @"""material_id"":{""type"":""integer""},""material_name"":{""type"":""string""}";

        public static List<Material> All(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();

        public static bool TryMaterial(Document doc, long? id, string name, string idField, string nameField,
            out Material material, out CommandResult failure)
        {
            material = null;
            failure = null;
            if (id.HasValue && !string.IsNullOrEmpty(name))
            {
                failure = CommandResult.Fail("INVALID_PARAMS: pass " + idField + " or " + nameField + ", not both.");
                return false;
            }
            if (!id.HasValue && string.IsNullOrWhiteSpace(name))
            {
                failure = CommandResult.Fail("INVALID_PARAMS: pass " + idField + " or " + nameField + ".");
                return false;
            }
            var all = All(doc);
            var matches = id.HasValue
                ? all.Where(m => RevitCompat.GetId(m.Id) == id.Value).ToList()
                : all.Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > 1) matches = matches.Where(m => string.Equals(m.Name, name, StringComparison.Ordinal)).ToList();
            if (matches.Count == 1)
            {
                material = matches[0];
                return true;
            }
            var options = all.OrderBy(m => string.IsNullOrEmpty(name) || m.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0 ? 1 : 0)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase);
            failure = CommandResult.Fail("MATERIAL_NOT_FOUND: no material " + (id.HasValue ? "has id " + id.Value : "is named '" + name + "'") + ".",
                new JObject
                {
                    ["valid_materials"] = new JArray(options.Take(MaxOptions).Select(m => new JObject
                    {
                        ["id"] = RevitCompat.GetId(m.Id),
                        ["name"] = m.Name
                    }))
                });
            return false;
        }

        public static Material ByName(Document doc, string name) =>
            All(doc).FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

        public static bool TryColor(JObject p, out Color color, out CommandResult failure)
        {
            color = null;
            failure = null;
            var r = p["red"];
            var g = p["green"];
            var b = p["blue"];
            if (r == null && g == null && b == null) return true;
            if (r == null || g == null || b == null)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: give red, green and blue together.");
                return false;
            }
            int red = r.Value<int>(), green = g.Value<int>(), blue = b.Value<int>();
            if (red < 0 || red > 255 || green < 0 || green > 255 || blue < 0 || blue > 255)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: red, green and blue must be 0 to 255.");
                return false;
            }
            color = new Color((byte)red, (byte)green, (byte)blue);
            return true;
        }

        public static bool TryRange(JObject p, string field, int min, int max, out int? value, out CommandResult failure)
        {
            value = p.Value<int?>(field);
            failure = value.HasValue && (value.Value < min || value.Value > max)
                ? CommandResult.Fail("INVALID_PARAMS: " + field + " must be " + min + " to " + max + ".")
                : null;
            return failure == null;
        }

        public static JToken ColorJson(Color c) =>
            c == null || !c.IsValid ? null : new JObject { ["red"] = c.Red, ["green"] = c.Green, ["blue"] = c.Blue };

        public static JToken IdOrNull(ElementId id) =>
            id == null || id == ElementId.InvalidElementId ? null : (JToken)RevitCompat.GetId(id);

        /// <summary>Property-set (asset) names in use, case-insensitively.</summary>
        public static HashSet<string> AssetNames(Document doc) =>
            new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(PropertySetElement)).Select(e => e.Name),
                StringComparer.OrdinalIgnoreCase);

        /// <summary>The asked name when free; a default name gets " 2", " 3"... appended until free.</summary>
        public static bool TryAssetName(Document doc, string asked, string fallback, out string name, out CommandResult failure)
        {
            failure = null;
            var used = AssetNames(doc);
            if (!string.IsNullOrWhiteSpace(asked))
            {
                name = asked.Trim();
                if (!used.Contains(name)) return true;
                failure = CommandResult.Fail("NAME_IN_USE: an asset named '" + name + "' already exists; pick another asset_name.");
                return false;
            }
            name = fallback;
            for (var i = 2; used.Contains(name); i++) name = fallback + " " + i;
            return true;
        }
    }
}
