// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), files
// src/shared/Lint/ViewNamingAnalyzer.cs, src/shared/Lint/FirmProfile.cs and src/shared/Lint/FirmProfileLibrary.cs,
// and the library-folder code in src/shared/Handlers/Lint/DetectFirmProfileHandler.cs, commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for B2M: profiles load from %LOCALAPPDATA%\B2M\firm-profiles\*.json only (rvt-mcp also read a
// firm-profiles folder beside the DLL, which it shipped empty, and %LOCALAPPDATA%\Bimwright\...); unreadable profile
// files are reported instead of skipped silently; profile regexes run with a timeout; Suggest fills a pattern's
// {NN} and {Name} slots from the current name (rvt-mcp returned the bare pattern, e.g. "L{NN}-{Name}", as the suggestion).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using B2M.Revit.Core;
using Newtonsoft.Json;

namespace B2M.Revit.Commands.Lint
{
    internal static class NamingLint
    {
        public const double DominantCoverageThreshold = 0.50;
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
        private static readonly Regex Delimiters = new Regex(@"([-_\s]+)");

        private static readonly HashSet<string> ReservedTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "3D", "Plan", "Section", "Sheet", "Elevation", "Detail", "Area", "RCP"
        };

        public static string ProfileFolder => Path.Combine(B2MPaths.Root, "firm-profiles");

        /// <summary>A name as a pattern: digits become {NN}, words {Name}, L01 becomes L{NN}; reserved words and delimiters stay.</summary>
        public static string Tokenize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var sb = new StringBuilder();
            foreach (var part in Delimiters.Split(name))
            {
                if (part.Length == 0) continue;
                if (Regex.IsMatch(part, @"^[-_\s]+$")) sb.Append(part);
                else if (ReservedTokens.Contains(part)) sb.Append(part);
                else if (Regex.IsMatch(part, @"^\d+$")) sb.Append("{NN}");
                else if (Regex.IsMatch(part, @"^[A-Za-z]+$")) sb.Append("{Name}");
                else
                {
                    var m = Regex.Match(part, @"^([A-Za-z]+)(\d+)$");
                    sb.Append(m.Success ? m.Groups[1].Value + "{NN}" : part);
                }
            }
            return sb.ToString();
        }

        private static int CountTokens(string pattern) => Regex.Split(pattern, @"[-_\s]+").Count(p => p.Length > 0);

        private static int Levenshtein(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;
            var d = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (var j = 0; j <= b.Length; j++) d[0, j] = j;
            for (var i = 1; i <= a.Length; i++)
                for (var j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }

        public sealed class Analysis
        {
            public int Total;
            public List<(string pattern, string[] examples, int count, double coverage)> Patterns = new List<(string, string[], int, double)>();
            public string Dominant;
            /// <summary>Names close to the dominant pattern (token count within one) but not matching it, nearest first.</summary>
            public List<(string name, int distance)> Outliers = new List<(string, int)>();
        }

        public static Analysis Analyze(IEnumerable<string> names)
        {
            var list = names?.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray() ?? new string[0];
            var result = new Analysis { Total = list.Length };
            if (list.Length == 0) return result;
            var tokenized = list.Select(n => (name: n, pattern: Tokenize(n))).ToList();
            foreach (var g in tokenized.GroupBy(x => x.pattern).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
                result.Patterns.Add((g.Key, g.Take(3).Select(x => x.name).ToArray(), g.Count(), Math.Round((double)g.Count() / list.Length, 4)));
            if (result.Patterns[0].coverage < DominantCoverageThreshold) return result;
            result.Dominant = result.Patterns[0].pattern;
            var tokens = CountTokens(result.Dominant);
            var example = result.Patterns[0].examples.FirstOrDefault() ?? "";
            result.Outliers = tokenized.Where(x => x.pattern != result.Dominant && Math.Abs(CountTokens(x.pattern) - tokens) <= 1)
                .Select(x => (x.name, Levenshtein(x.name, example)))
                .OrderBy(x => x.Item2).ThenBy(x => x.name, StringComparer.Ordinal)
                .ToList();
            return result;
        }

        /// <summary>
        /// Fills the pattern's slots from the name: {NN} takes the name's next number, {Name} its next word (the last
        /// {Name} takes all remaining words), literals stay. Null when the name lacks a number or word the pattern needs.
        /// </summary>
        public static string Suggest(string current, string pattern)
        {
            var literals = new HashSet<string>(Regex.Split(pattern, @"[-_\s]+|\{NN\}|\{Name\}").Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
            var numbers = new Queue<string>(Regex.Matches(current, @"\d+").Cast<Match>().Select(m => m.Value));
            var words = new Queue<string>(Regex.Matches(current, @"[A-Za-z]+").Cast<Match>().Select(m => m.Value).Where(w => !literals.Contains(w)));
            var slots = Regex.Matches(pattern, @"\{Name\}").Count;
            var sb = new StringBuilder();
            var last = 0;
            foreach (Match slot in Regex.Matches(pattern, @"\{NN\}|\{Name\}"))
            {
                sb.Append(pattern, last, slot.Index - last);
                last = slot.Index + slot.Length;
                if (slot.Value == "{NN}")
                {
                    if (numbers.Count == 0) return null;
                    sb.Append(numbers.Dequeue());
                    continue;
                }
                if (words.Count == 0) return null;
                slots--;
                sb.Append(slots == 0 ? string.Join(" ", words) : words.Dequeue());
                if (slots == 0) words.Clear();
            }
            sb.Append(pattern, last, pattern.Length - last);
            return sb.ToString();
        }

        public sealed class FirmProfile
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public List<Hint> MatchHints { get; set; } = new List<Hint>();
            public RuleSet Rules { get; set; } = new RuleSet();
        }

        public sealed class Hint
        {
            public string Kind { get; set; } // sheet_prefix, view_dominant, level_pattern
            public string Regex { get; set; }
            public double Weight { get; set; }
        }

        public sealed class RuleSet
        {
            public string ViewName { get; set; }
            public string SheetNumber { get; set; }
            public string Level { get; set; }
        }

        public static List<FirmProfile> LoadProfiles(out List<string> problems)
        {
            problems = new List<string>();
            var byId = new Dictionary<string, FirmProfile>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(ProfileFolder)) return new List<FirmProfile>();
            foreach (var file in Directory.GetFiles(ProfileFolder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var profile = JsonConvert.DeserializeObject<FirmProfile>(File.ReadAllText(file));
                    if (profile == null || string.IsNullOrWhiteSpace(profile.Id)) problems.Add(Path.GetFileName(file) + ": no id");
                    else byId[profile.Id] = profile;
                }
                catch (Exception ex)
                {
                    problems.Add(Path.GetFileName(file) + ": " + ex.Message);
                }
            }
            return byId.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>The best profile whose matching hint weights sum to at least 0.5; ties go to more hints, then id.</summary>
        public static (FirmProfile profile, double confidence, List<string> hints)? Match(List<FirmProfile> profiles, string sheetPrefix, string viewDominant, string levelPattern)
        {
            (FirmProfile, double, List<string>)? best = null;
            foreach (var profile in profiles)
            {
                double score = 0;
                var matched = new List<string>();
                foreach (var hint in profile.MatchHints ?? new List<Hint>())
                {
                    var target = hint.Kind == "sheet_prefix" ? sheetPrefix : hint.Kind == "view_dominant" ? viewDominant : hint.Kind == "level_pattern" ? levelPattern : null;
                    if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(hint.Regex)) continue;
                    try
                    {
                        if (!Regex.IsMatch(target, hint.Regex, RegexOptions.None, RegexTimeout)) continue;
                    }
                    catch (Exception ex) when (ex is ArgumentException || ex is RegexMatchTimeoutException)
                    {
                        continue; // A malformed or runaway regex skips the hint, not the profile.
                    }
                    score += hint.Weight;
                    matched.Add(hint.Kind);
                }
                if (score < 0.5) continue;
                if (best == null || score > best.Value.Item2 + 0.001
                    || (Math.Abs(score - best.Value.Item2) <= 0.001 && matched.Count > best.Value.Item3.Count))
                    best = (profile, score, matched);
            }
            return best;
        }
    }
}
