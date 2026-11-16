// New in B2M (Step 10, F1): what an installed template (or any project file) offers the fit-out: its loadable family
// types (category, family, type, placement, Width/Depth/Length/Height/Diameter in mm where the type has them) and its
// MEP types (pipe, piping system, duct, duct system, conduit, cable tray, distribution system, voltage). The file is
// opened in the background and closed without saving (SourceDocument); the full listing is cached per file and Revit
// year in %LOCALAPPDATA%\B2M\cache\file-types (TypeListCache), so later calls do not open it again. Only the response
// is filtered. Ids are the source file's own, for copy_types_from_file's source_id.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using B2M.Revit.Core.Families;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Families
{
    public sealed class ListTypesInFileHandler : IRevitCommand
    {
        private const int DefaultMaxFamilyTypes = 1500;

        public string Name => "list_types_in_file";
        public string Description =>
            "List the family types and MEP types in a Revit template or project file (absolute path to a .rte or .rvt, e.g. from "
            + "list_template_files), without changing anything: the file is opened in the background and closed unsaved, and the listing "
            + "is cached per file. family_types: id, category (OST_...), family, type, placement (OneLevelBased, WorkPlaneBased...), "
            + "dims_mm. mep_types: pipe_type, pipe_system_type, duct_type, duct_system_type, conduit_type, cable_tray_type, "
            + "distribution_system, voltage_type. Filters: kinds, categories, name_contains (family or type name), include_annotation. "
            + "Ids are the file's own, for copy_types_from_file.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""path"":{""type"":""string"",""description"":""Absolute path to a .rte or .rvt file""},
            ""kinds"":{""type"":""array"",""items"":{""type"":""string""},""description"":""family_type and/or MEP kinds; default all""},
            ""categories"":{""type"":""array"",""items"":{""type"":""string""},""description"":""Built-in categories for family types, e.g. OST_PlumbingFixtures""},
            ""name_contains"":{""type"":""string""},
            ""include_annotation"":{""type"":""boolean"",""description"":""Also list tags and other annotation family types (default false)""},
            ""max_family_types"":{""type"":""integer"",""description"":""Default 1500""},
            ""refresh"":{""type"":""boolean"",""description"":""Ignore the cache and open the file again""}},
            ""required"":[""path""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            if (!SourceDocument.TryResolvePath(p.Value<string>("path"), out var fullPath, out var info, out var failure)) return failure;

            var kinds = Strings(p["kinds"]);
            var badKind = kinds?.FirstOrDefault(k => !FileTypeKinds.IsKnown(k));
            if (badKind != null) return CommandResult.Fail("INVALID_PARAMS: unknown kind '" + badKind + "'. Valid kinds: " + FileTypeKinds.AllText + ".");
            var categories = Strings(p["categories"]);
            foreach (var category in categories ?? new List<string>())
                if (!Lookup.TryBuiltInCategory(category, out _, out failure)) return failure;
            var max = p.Value<int?>("max_family_types") ?? DefaultMaxFamilyTypes;
            if (max < 1) return CommandResult.Fail("INVALID_PARAMS: max_family_types must be at least 1.");

            var year = app.Application.VersionNumber;
            var key = TypeListCache.Key(fullPath, info.Length, info.LastWriteTimeUtc, year);
            var listing = (p.Value<bool?>("refresh") ?? false) ? null : TypeListCache.TryRead(TypeListCache.DefaultFolder, key);
            var cached = listing != null;
            if (listing == null)
            {
                if (!SourceDocument.TryOpen(app, fullPath, out var source, out failure)) return failure;
                using (source)
                {
                    try
                    {
                        listing = Build(source.Doc);
                    }
                    catch (Exception ex)
                    {
                        return CommandResult.Fail("READ_FAILED: could not list the types in '" + System.IO.Path.GetFileName(fullPath) + "': " + ex.Message);
                    }
                    // A file Revit already had open may hold unsaved changes, so only a fresh read of the file is cached.
                    if (source.OpenedHere) TypeListCache.Write(TypeListCache.DefaultFolder, key, listing);
                }
            }
            return CommandResult.Ok(Filter(listing, fullPath, cached, kinds, categories, p.Value<string>("name_contains"),
                p.Value<bool?>("include_annotation") ?? false, max));
        }

        /// <summary>Everything in the file, unfiltered (this is what is cached).</summary>
        private static JObject Build(Document doc)
        {
            var family = new JArray();
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .OrderBy(s => s.Category?.Name).ThenBy(s => s.FamilyName).ThenBy(s => s.Name);
            foreach (var symbol in symbols)
            {
                var o = FileTypeKinds.DescribeFamilyType(symbol);
                o["annotation"] = symbol.Category == null || symbol.Category.CategoryType != CategoryType.Model;
                family.Add(o);
            }
            var mep = new JObject();
            foreach (var kind in FileTypeKinds.Mep)
                mep[kind.Key] = new JArray(FileTypeKinds.TypesOf(doc, kind.Key).OrderBy(t => t.Name).Select(t => (JToken)FileTypeKinds.DescribeMep(doc, t)));
            return new JObject
            {
                ["document_title"] = doc.Title,
                ["listed_utc"] = DateTime.UtcNow.ToString("o"),
                ["family_types"] = family,
                ["mep_types"] = mep
            };
        }

        private static JObject Filter(JObject listing, string fullPath, bool cached, List<string> kinds, List<string> categories, string nameContains,
            bool includeAnnotation, int max)
        {
            bool Wanted(string kind) => kinds == null || kinds.Contains(kind);
            bool NameOk(params string[] names) =>
                string.IsNullOrEmpty(nameContains) || names.Any(n => n != null && n.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0);

            var result = new JObject
            {
                ["path"] = fullPath,
                ["file_name"] = System.IO.Path.GetFileName(fullPath),
                ["from_cache"] = cached,
                ["listed_utc"] = listing["listed_utc"]
            };
            if (Wanted(FileTypeKinds.FamilyType))
            {
                var matches = ((JArray)listing["family_types"]).OfType<JObject>()
                    .Where(o => includeAnnotation || o.Value<bool?>("annotation") != true)
                    .Where(o => categories == null || categories.Contains(o.Value<string>("category")))
                    .Where(o => NameOk(o.Value<string>("family"), o.Value<string>("type")))
                    .ToList();
                var page = new JArray();
                foreach (var o in matches.Take(max))
                {
                    var copy = (JObject)o.DeepClone();
                    copy.Remove("annotation");
                    page.Add(copy);
                }
                result["family_types"] = page;
                result["family_types_total"] = matches.Count;
                result["family_types_truncated"] = matches.Count > max;
            }
            var mep = new JObject();
            foreach (var kind in FileTypeKinds.Mep.Select(m => m.Key).Where(Wanted))
                mep[kind] = new JArray(((JArray)listing["mep_types"]?[kind] ?? new JArray()).OfType<JObject>().Where(o => NameOk(o.Value<string>("name"))));
            if (mep.Count > 0) result["mep_types"] = mep;
            return result;
        }

        private static List<string> Strings(JToken token) =>
            token is JArray array ? array.Where(t => t.Type == JTokenType.String).Select(t => t.Value<string>().Trim()).Where(s => s.Length > 0).ToList() : null;
    }
}
