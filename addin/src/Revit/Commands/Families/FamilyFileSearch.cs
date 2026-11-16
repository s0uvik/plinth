// New in B2M: rvt-mcp can only load a family from a path the caller already knows. This finds .rfa files by name
// under the caller's folders, B2M's own family folder (%LOCALAPPDATA%\B2M\families, where ensure_family_type saves
// the families it creates) and the Revit content library of the running year. The scan is breadth first in name
// order, so the shallowest match wins and repeated runs give the same answer; it stops after a fixed number of
// entries per root, skips folders it cannot read and never follows junctions or symbolic links. The library root
// holds every installed content language (English, English-Imperial...), so all of them are searched.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.RegularExpressions;
using B2M.Revit.Bridge;
using B2M.Revit.Commands.Parameters;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Families
{
    internal static class FamilyFileSearch
    {
        /// <summary>Files and folders looked at per root before the scan stops.</summary>
        public const int MaxEntriesPerRoot = 20000;

        public const int MaxSearchFolders = 20;

        public const string SearchFoldersSchema =
            @"""search_folders"":{""type"":""array"",""items"":{""type"":""string""},""description"":""absolute folders searched before the defaults""}";

        /// <summary>Revit's numbered backups, e.g. Column.0001.rfa, are never families to load.</summary>
        private static readonly Regex Backup = new Regex(@"\.\d{4}\.rfa$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public sealed class Root
        {
            public string Folder;

            /// <summary>"given", "b2m" or "library".</summary>
            public string Kind;

            public bool Exists;
            public int Scanned;
            public bool Truncated;

            public JObject ToJson() => new JObject
            {
                ["folder"] = Folder,
                ["kind"] = Kind,
                ["exists"] = Exists,
                ["entries_scanned"] = Scanned,
                ["truncated"] = Truncated
            };
        }

        public sealed class Hit
        {
            public string Path;
            public Root Root;

            public string FileName => System.IO.Path.GetFileName(Path);
            public string FamilyName => System.IO.Path.GetFileNameWithoutExtension(Path);
            public string Folder => System.IO.Path.GetDirectoryName(Path);

            public JObject ToJson() => new JObject { ["family_name"] = FamilyName, ["file_name"] = FileName, ["folder"] = Folder };
        }

        /// <summary>Where ensure_family_type saves the families it creates.</summary>
        public static string B2MFamiliesFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "B2M", "families");

        public static string LibraryFolder(string year) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "RVT " + year, "Libraries");

        public static string TemplatesFolder(string year) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "RVT " + year,
                "Family Templates", "English");

        /// <summary>The caller's folders (absolute, existing) first, then B2M's folder, then the year's library.</summary>
        public static bool TryRoots(JToken searchFolders, string year, out List<Root> roots, out CommandResult failure)
        {
            roots = new List<Root>();
            failure = null;
            var given = searchFolders as JArray ?? new JArray();
            if (given.Count > MaxSearchFolders)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: search_folders takes at most " + MaxSearchFolders + " folders.");
                return false;
            }
            for (var i = 0; i < given.Count; i++)
            {
                var folder = given[i].Type == JTokenType.String ? given[i].Value<string>().Trim() : null;
                if (!ParametersHelpers.IsAbsolute(folder))
                {
                    failure = CommandResult.Fail("INVALID_PATH: search_folders[" + i + "] must be an absolute folder path such as D:\\Families.");
                    return false;
                }
                try { folder = Path.GetFullPath(folder); }
                catch (Exception ex)
                {
                    failure = CommandResult.Fail("INVALID_PATH: search_folders[" + i + "]: " + ex.Message);
                    return false;
                }
                if (!Directory.Exists(folder))
                {
                    failure = CommandResult.Fail("FOLDER_NOT_FOUND: search_folders[" + i + "] does not exist or cannot be read.",
                        new JObject { ["folder"] = folder });
                    return false;
                }
                Add(roots, folder, "given");
            }
            Add(roots, B2MFamiliesFolder, "b2m");
            Add(roots, LibraryFolder(year), "library");
            return true;
        }

        /// <summary>Every family file under the roots, in root order then breadth-first name order (bounded per root).</summary>
        public static List<Hit> ListAll(IEnumerable<Root> roots)
        {
            var hits = new List<Hit>();
            foreach (var root in roots)
                Scan(root, path =>
                {
                    hits.Add(new Hit { Path = path, Root = root });
                    return true;
                });
            return hits;
        }

        /// <summary>The first file named exactly fileName (case-insensitive), or null.</summary>
        public static Hit FindExact(IEnumerable<Hit> files, string fileName) =>
            files.FirstOrDefault(h => string.Equals(h.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        /// <summary>Family files that match, up to limit, scanning lazily. truncated: more may exist.</summary>
        public static List<Hit> FindMatching(IEnumerable<Root> roots, Func<Hit, bool> match, int limit, out bool truncated)
        {
            var hits = new List<Hit>();
            var limitReached = false;
            var rootList = roots.ToList();
            foreach (var root in rootList)
            {
                Scan(root, path =>
                {
                    var hit = new Hit { Path = path, Root = root };
                    if (!match(hit)) return true;
                    if (hits.Count >= limit)
                    {
                        limitReached = true;
                        return false;
                    }
                    hits.Add(hit);
                    return true;
                });
                if (limitReached) break;
            }
            truncated = limitReached || rootList.Any(r => r.Truncated);
            return hits;
        }

        private static void Add(List<Root> roots, string folder, string kind)
        {
            if (roots.Any(r => string.Equals(r.Folder.TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))) return;
            roots.Add(new Root { Folder = folder, Kind = kind, Exists = Directory.Exists(folder) });
        }

        /// <summary>Breadth-first over the root; visit gets each .rfa path (not backups) and returns false to stop.</summary>
        private static void Scan(Root root, Func<string, bool> visit)
        {
            if (!root.Exists) return;
            var queue = new Queue<DirectoryInfo>();
            queue.Enqueue(new DirectoryInfo(root.Folder));
            while (queue.Count > 0)
            {
                var dir = queue.Dequeue();
                FileInfo[] files;
                DirectoryInfo[] subfolders;
                try
                {
                    files = dir.GetFiles("*.rfa");
                    subfolders = dir.GetDirectories();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is SecurityException)
                {
                    continue; // Unreadable folder: skip it, keep scanning the rest.
                }
                foreach (var file in files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (++root.Scanned > MaxEntriesPerRoot)
                    {
                        root.Truncated = true;
                        return;
                    }
                    // GetFiles("*.rfa") also matches longer extensions through 8.3 names; keep exact .rfa only.
                    if (!string.Equals(file.Extension, ".rfa", StringComparison.OrdinalIgnoreCase) || Backup.IsMatch(file.Name)) continue;
                    if (!visit(file.FullName)) return;
                }
                foreach (var sub in subfolders.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (++root.Scanned > MaxEntriesPerRoot)
                    {
                        root.Truncated = true;
                        return;
                    }
                    bool linked;
                    try { linked = (sub.Attributes & FileAttributes.ReparsePoint) != 0; }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException) { continue; }
                    if (!linked) queue.Enqueue(sub);
                }
            }
        }
    }
}
