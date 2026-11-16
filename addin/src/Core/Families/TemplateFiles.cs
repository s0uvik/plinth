// New in B2M (Step 10, F1): Revit 2026 installs no local family library, so fit-out families and MEP system types come
// from the project templates Revit installs under %PROGRAMDATA%\Autodesk\RVT <year>\Templates (one folder per content
// language, a few loose .rte files at the top). Listing them is plain file work, kept here so it can be unit-tested.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace B2M.Revit.Core.Families
{
    public static class TemplateFiles
    {
        public sealed class Entry
        {
            public string Name;
            public string Path;

            /// <summary>The language folder (e.g. "English"), or "" for a template at the top of the Templates folder.</summary>
            public string Language;

            public long SizeBytes;
            public DateTime ModifiedUtc;
        }

        /// <summary>%PROGRAMDATA%\Autodesk\RVT &lt;year&gt;\Templates.</summary>
        public static string Root(string programData, string year) =>
            Path.Combine(programData, "Autodesk", "RVT " + year, "Templates");

        /// <summary>True for a project template or project file (.rte, .rvt), the files whose types can be listed and copied.</summary>
        public static bool IsProjectFile(string path)
        {
            var extension = Path.GetExtension(path ?? "");
            return string.Equals(extension, ".rte", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".rvt", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Every .rte directly in root and in its language folders (one level deep), ordered by language then name.
        /// language, when given, keeps only that folder (case-insensitive). A missing root gives an empty list.
        /// </summary>
        public static List<Entry> List(string root, string language = null)
        {
            var result = new List<Entry>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return result;
            var folders = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("", root) };
            try
            {
                folders.AddRange(Directory.GetDirectories(root).Select(d => new KeyValuePair<string, string>(Path.GetFileName(d), d)));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // An unreadable root lists only its own files.
            }
            foreach (var folder in folders)
            {
                if (!string.IsNullOrEmpty(language) && !string.Equals(folder.Key, language, StringComparison.OrdinalIgnoreCase)) continue;
                string[] files;
                try { files = Directory.GetFiles(folder.Value, "*.rte"); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }
                foreach (var file in files)
                {
                    var info = new FileInfo(file);
                    result.Add(new Entry
                    {
                        Name = info.Name,
                        Path = info.FullName,
                        Language = folder.Key,
                        SizeBytes = info.Length,
                        ModifiedUtc = info.LastWriteTimeUtc
                    });
                }
            }
            return result.OrderBy(e => e.Language, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
