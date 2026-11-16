// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), the family lookup repeated in
// src/shared/Handlers/Families/UnloadFamilyHandler.cs, GetFamilyInstancesHandler.cs, ListFamilyTypesInFamilyHandler.cs
// and ExportFamilyToPathHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: one lookup by integer id or name whose misses carry the valid families; no reflection fallbacks
// (Family.GetFamilySymbolIds exists in every supported year). The family load options and the type-parameter setter
// (from LoadFamilyFromPathHandler.cs and DuplicateFamilyTypeHandler.cs) live here so ensure_family_type reuses them.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using B2M.Revit.Bridge;
using B2M.Revit.Commands.Parameters;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Families
{
    internal static class FamiliesHelpers
    {
        private const int MaxOptions = 100;

        public const string FamilySchema = @"""family_id"":{""type"":""integer""},""family_name"":{""type"":""string""}";

        /// <summary>A loadable or in-place family by element id, else by name (case-insensitive, must be unique).</summary>
        public static bool TryFamily(Document doc, long? id, string name, out Family family, out CommandResult failure)
        {
            family = null;
            failure = null;
            if (id.HasValue && !string.IsNullOrEmpty(name))
            {
                failure = CommandResult.Fail("INVALID_PARAMS: pass family_id or family_name, not both.");
                return false;
            }
            if (!id.HasValue && string.IsNullOrWhiteSpace(name))
            {
                failure = CommandResult.Fail("INVALID_PARAMS: pass family_id or family_name.");
                return false;
            }
            var all = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().ToList();
            List<Family> matches;
            if (id.HasValue)
                matches = all.Where(f => RevitCompat.GetId(f.Id) == id.Value).ToList();
            else
            {
                matches = all.Where(f => string.Equals(f.Name, name, StringComparison.Ordinal)).ToList();
                if (matches.Count == 0) matches = all.Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (matches.Count == 1)
            {
                family = matches[0];
                return true;
            }
            failure = CommandResult.Fail(matches.Count == 0
                    ? "FAMILY_NOT_FOUND: no loadable or in-place family " + (id.HasValue ? "has id " + id.Value : "is named '" + name + "'") + "."
                    : "FAMILY_AMBIGUOUS: " + matches.Count + " families are named '" + name + "'; pass family_id.",
                new JObject { ["valid_families"] = Describe(matches.Count > 1 ? matches : Closest(all, name)) });
            return false;
        }

        public static JArray Describe(IEnumerable<Family> families) =>
            new JArray(families.Take(MaxOptions).Select(f => new JObject
            {
                ["id"] = RevitCompat.GetId(f.Id),
                ["name"] = f.Name,
                ["category"] = f.FamilyCategory?.Name,
                ["builtInCategory"] = Lookup.BuiltInName(f.FamilyCategory)
            }));

        /// <summary>Families whose name contains the asked name first, then the rest by name.</summary>
        private static IEnumerable<Family> Closest(IEnumerable<Family> all, string name) =>
            all.OrderBy(f => string.IsNullOrEmpty(name) || f.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0 ? 1 : 0)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase);

        public static string Kind(Family family) => family.IsInPlace ? "inplace" : "loadable";

        /// <summary>An element type (family type or system type) by id; a miss names what the id is instead.</summary>
        public static bool TryType(Document doc, long id, string field, out ElementType type, out CommandResult failure)
        {
            var element = doc.GetElement(RevitCompat.ToElementId(id));
            type = element as ElementType;
            failure = type != null ? null : CommandResult.Fail("TYPE_NOT_FOUND: " + field + " " + id
                + (element == null ? " does not exist." : " is a " + element.GetType().Name + ", not a type."));
            return type != null;
        }

        public static string FamilyNameOf(ElementType type) => (type as FamilySymbol)?.FamilyName ?? type.FamilyName;

        /// <summary>Another type that a new name would clash with: a sibling in the family, or a system type of the same class.</summary>
        public static ElementType NameClash(Document doc, ElementType type, string newName)
        {
            IEnumerable<ElementType> others;
            if (type is FamilySymbol symbol && symbol.Family != null)
                others = symbol.Family.GetFamilySymbolIds().Select(doc.GetElement).OfType<ElementType>();
            else
                others = new FilteredElementCollector(doc).OfClass(type.GetType()).Cast<ElementType>();
            return others.FirstOrDefault(t => t.Id != type.Id && string.Equals(t.Name, newName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Sets type parameters from {name: value} in wire units (see ParametersHelpers.TrySet). Runs inside the caller's
        /// open transaction; on failure the caller rolls back. The error has no trailing "nothing changed" note.
        /// </summary>
        public static bool TrySetTypeParameters(Document doc, ElementType type, JObject values, out JObject set, out string error,
            out JObject errorData)
        {
            set = new JObject();
            error = null;
            errorData = null;
            foreach (var prop in values.Properties())
            {
                var parameter = ParametersHelpers.FindByName(type, prop.Name);
                if (parameter == null)
                {
                    error = "PARAMETER_NOT_FOUND: the type has no parameter named '" + prop.Name + "'.";
                    errorData = new JObject { ["writable_parameters"] = ParametersHelpers.ParameterNames(type) };
                    return false;
                }
                if (!ParametersHelpers.TrySet(doc, parameter, prop.Value, "auto", out _, out var setError))
                {
                    error = "SET_FAILED: '" + prop.Name + "': " + setError + ".";
                    return false;
                }
                set[parameter.Definition.Name] = ParametersHelpers.Read(doc, parameter);
            }
            return true;
        }

        public static double[] PointMm(XYZ p) => new[] { Mm.FromFeet(p.X), Mm.FromFeet(p.Y), Mm.FromFeet(p.Z) };

        /// <summary>Paging over one list: start index, page size and what is left.</summary>
        public static JObject Page<T>(IList<T> items, int start, int size, Func<T, JToken> map, out JArray page)
        {
            page = new JArray(items.Skip(start).Take(size).Select(map));
            var next = start + page.Count;
            return new JObject
            {
                ["total"] = items.Count,
                ["start_index"] = start,
                ["returned"] = page.Count,
                ["truncated"] = next < items.Count,
                ["next_index"] = next < items.Count ? (JToken)next : null
            };
        }
    }

    /// <summary>Answers Revit when a loaded family is found again: reload only when asked to.</summary>
    internal sealed class FamilyLoadOptions : IFamilyLoadOptions
    {
        private readonly bool _overwrite;
        private readonly bool _overwriteValues;

        public FamilyLoadOptions(bool overwrite, bool overwriteValues)
        {
            _overwrite = overwrite;
            _overwriteValues = overwriteValues;
        }

        public bool FamilyWasFound { get; private set; }

        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            FamilyWasFound = true;
            overwriteParameterValues = _overwriteValues;
            return _overwrite;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            FamilyWasFound = true;
            source = FamilySource.Family;
            overwriteParameterValues = _overwriteValues;
            return _overwrite;
        }
    }
}
