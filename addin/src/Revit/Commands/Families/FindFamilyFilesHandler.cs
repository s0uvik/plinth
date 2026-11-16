// New in B2M: lists .rfa family files on disk whose name contains some text, searching the caller's folders, B2M's
// family folder and every language folder of the running Revit year's content library (the same roots and scan
// bounds ensure_family_type uses). An optional category filter is a quick file-name heuristic only (the file or
// its folder names a word such as "column" or "beam"); no file is opened. Read-only; needs no open document.

using System.Linq;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Families
{
    public sealed class FindFamilyFilesHandler : IRevitCommand
    {
        private const int DefaultLimit = 50;
        private const int MaxLimit = 500;

        public string Name => "find_family_files";
        public string Description =>
            "Find .rfa family files whose file name contains name_contains (case-insensitive; may be empty when category is given). "
            + "Searches search_folders (absolute) first, then %LOCALAPPDATA%\\B2M\\families, then the Revit content library of this "
            + "Revit year (all languages), at most " + FamilyFileSearch.MaxEntriesPerRoot + " entries per folder. category "
            + "(structural_column, structural_framing) keeps files whose name or folder suggests it; files are not opened. "
            + "Returns [{family_name, file_name, folder}] and truncated.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""name_contains"":{""type"":""string""},
            ""category"":{""type"":""string"",""enum"":[""structural_column"",""structural_framing""]},
            " + FamilyFileSearch.SearchFoldersSchema + @",
            ""limit"":{""type"":""integer"",""description"":""1-500, default 50""}},""required"":[""name_contains""]}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var text = (p.Value<string>("name_contains") ?? "").Trim();
            var categoryKey = p.Value<string>("category");
            var recipe = categoryKey == null ? null : FamilyRecipe.Find(categoryKey);
            if (categoryKey != null && recipe == null)
                return CommandResult.Fail("INVALID_PARAMS: category must be one of " + string.Join(", ", FamilyRecipe.All.Select(r => r.Key)) + ".");
            if (text.Length == 0 && recipe == null) return CommandResult.Fail("INVALID_PARAMS: name_contains must not be empty unless category is given.");
            var limit = p.Value<int?>("limit") ?? DefaultLimit;
            if (limit < 1 || limit > MaxLimit) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to " + MaxLimit + ".");
            if (!FamilyFileSearch.TryRoots(p["search_folders"], app.Application.VersionNumber, out var roots, out var failure)) return failure;

            var hits = FamilyFileSearch.FindMatching(roots, hit =>
                    hit.FamilyName.IndexOf(text, System.StringComparison.OrdinalIgnoreCase) >= 0
                    && (recipe == null || recipe.LooksLikeCategory(hit.FileName, hit.Folder)),
                limit, out var truncated);
            return CommandResult.Ok(new JObject
            {
                ["files"] = new JArray(hits.Select(h => h.ToJson())),
                ["count"] = hits.Count,
                ["truncated"] = truncated,
                ["roots"] = new JArray(roots.Select(r => r.ToJson()))
            });
        }
    }
}
