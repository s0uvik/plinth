// New in B2M: the Revit-free decisions behind ensure_family_type for doors and windows, and behind type_mark on any
// category. Drawings name openings by MARK and SIZE (D = 900 x 2100, W1 = 1000 x 1200), so the build needs a type with
// exactly that width/height and Type Mark, without ever editing an existing type (duplicate before edit, see
// SharedEditRule). The Revit side (addin/src/Revit/Commands/Families/OpeningFamilies.cs) reads the facts (families,
// their types' sizes, marks and Function) and applies these decisions.
//
// TypeMatchRule, inside one family:
//   type_name given  - the type of that name is used when its size matches (and its Type Mark, when one is asked);
//                      a different size is TYPE_DIMENSION_MISMATCH, a different mark TYPE_MARK_MISMATCH (never edited).
//                      When no type has that name, a new one is made with that name.
//   type_name omitted - a type with this size (and this mark, when asked) is used; with several, the one named
//                      "<w> x <h>mm" (or "<w> x <h>mm <mark>"), then one without a mark (when none is asked), then by
//                      name. Otherwise a new type "<w> x <h>mm" is made, or "<w> x <h>mm <mark>" when that name is taken
//                      (e.g. by a type of this size carrying another mark, which is left untouched).
// FamilyChoiceRule, when family_name is omitted: every loaded family of the category that can carry the type is
// scored from its name, its type names and its types' Function (Interior/Exterior):
//   doors   - single leaf up to 1200 mm wide, double leaf above (+3 right leaf, -5 wrong leaf); Flush/Passage/Interior
//             +2 for an interior door; Exterior/Entrance +2 only with hint "exterior" (and -2 without it); special
//             kinds (sliding, bifold, pocket, overhead, curtain wall, opening...) -4 unless the hint names them;
//   windows - Fixed/Casement +2; special kinds (skylight, roof, dormer, curtain, louver...) -4 unless hinted;
//   both    - each other hint word found in the names +6 (the caller asked); a type of this size already there +2, carrying the
//             asked mark too +2 more.
// The unique best family with a score of 0 or more is chosen and the result says why, with the alternatives. A tie
// for best, or a best below 0, is FAMILY_CHOICE_NEEDED listing the candidates: never a silent pick.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Core.Families
{
    public enum OpeningKind
    {
        Door,
        Window
    }

    /// <summary>One type of a family as the decisions see it.</summary>
    public sealed class TypeFacts
    {
        public string Name { get; set; }

        /// <summary>Its sizes in mm, in the request's order (b, h or width, height); an entry is null when it cannot be read.</summary>
        public double?[] SizesMm { get; set; }

        /// <summary>Its Type Mark; null or empty when it has none.</summary>
        public string Mark { get; set; }

        /// <summary>"interior", "exterior" or null, from the type's Function parameter.</summary>
        public string Function { get; set; }
    }

    /// <summary>The type a call asks for.</summary>
    public sealed class TypeRequest
    {
        /// <summary>The caller's type_name; null to use the default name rule.</summary>
        public string ExplicitName { get; set; }

        /// <summary>"&lt;w&gt; x &lt;h&gt;mm" (TypeMatchRule.DefaultName); used when ExplicitName is null.</summary>
        public string DefaultName { get; set; }

        public double[] SizesMm { get; set; }

        /// <summary>Labels of the sizes for messages and data, e.g. width, height.</summary>
        public string[] Labels { get; set; }

        /// <summary>The asked Type Mark; null when none is asked.</summary>
        public string Mark { get; set; }
    }

    public enum TypeStep
    {
        UseExisting,
        CreateNew,
        Refuse
    }

    public sealed class TypeDecision
    {
        public TypeStep Step { get; internal set; }

        /// <summary>The type to use (UseExisting) or the type that blocks the request (Refuse); -1 otherwise.</summary>
        public int Index { get; internal set; } = -1;

        /// <summary>The existing type's name, or the new type's name.</summary>
        public string TypeName { get; internal set; }

        /// <summary>UseExisting: "name", "size" or "size_and_mark".</summary>
        public string MatchedBy { get; internal set; }

        /// <summary>"CODE: message" when Step is Refuse.</summary>
        public string Error { get; internal set; }

        public JObject Data { get; internal set; }

        public string Code => Error == null ? null : Error.Substring(0, Math.Max(0, Error.IndexOf(':')));
    }

    public static class TypeMatchRule
    {
        public const double ToleranceMm = 0.5;

        /// <summary>Characters Revit refuses in a type name.</summary>
        public const string InvalidNameChars = "\\:{}[]|;<>?`~";

        public static string DefaultName(double widthMm, double heightMm) => Number(widthMm) + " x " + Number(heightMm) + "mm";

        public static string Number(double mm) => mm.ToString("0.#", CultureInfo.InvariantCulture);

        public static string NormalizeMark(string mark) => (mark ?? "").Trim();

        /// <summary>Marks compare trimmed and ignoring letter case (drawings write w1 where a project may have W1); none equals empty.</summary>
        public static bool SameMark(string a, string b) => string.Equals(NormalizeMark(a), NormalizeMark(b), StringComparison.OrdinalIgnoreCase);

        public static bool SameSize(double?[] actual, double[] requested)
        {
            if (actual == null || requested == null || actual.Length != requested.Length) return false;
            for (var i = 0; i < requested.Length; i++)
                if (!actual[i].HasValue || Math.Abs(actual[i].Value - requested[i]) > ToleranceMm) return false;
            return true;
        }

        /// <summary>The type of that name: exact first, then ignoring letter case; -1 when none.</summary>
        public static int IndexByName(IList<TypeFacts> types, string name)
        {
            for (var i = 0; i < types.Count; i++)
                if (types[i].Name == name) return i;
            for (var i = 0; i < types.Count; i++)
                if (string.Equals(types[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>The type whose readable sizes are closest to the request (the best one to copy); -1 for no types.</summary>
        public static int ClosestIndex(IList<TypeFacts> types, double[] requested)
        {
            var best = -1;
            var bestDistance = double.MaxValue;
            for (var i = 0; i < types.Count; i++)
            {
                var sizes = types[i].SizesMm ?? new double?[0];
                var distance = 0.0;
                for (var k = 0; k < requested.Length; k++)
                    distance += k < sizes.Length && sizes[k].HasValue ? Math.Abs(sizes[k].Value - requested[k]) : 1e9;
                if (distance < bestDistance)
                {
                    best = i;
                    bestDistance = distance;
                }
            }
            return best;
        }

        /// <summary>Which type of the family serves the request: an existing one, a new one (and its name), or a refusal.</summary>
        public static TypeDecision Decide(IList<TypeFacts> types, string familyName, TypeRequest request)
        {
            if (types == null) throw new ArgumentNullException(nameof(types));
            if (request == null) throw new ArgumentNullException(nameof(request));
            var mark = request.Mark == null ? null : NormalizeMark(request.Mark);

            if (request.ExplicitName != null)
            {
                var index = IndexByName(types, request.ExplicitName);
                if (index < 0) return Create(request.ExplicitName);
                var type = types[index];
                if (!SameSize(type.SizesMm, request.SizesMm)) return SizeMismatch(type, index, familyName, request, "use another type_name");
                if (mark != null && !SameMark(type.Mark, mark))
                    return MarkMismatch(type, index, familyName, request, "use another type_name, or omit type_name so B2M finds or makes a type "
                        + "with this size and mark");
                return new TypeDecision { Step = TypeStep.UseExisting, Index = index, TypeName = type.Name, MatchedBy = "name" };
            }

            var matches = Enumerable.Range(0, types.Count)
                .Where(i => SameSize(types[i].SizesMm, request.SizesMm) && (mark == null || SameMark(types[i].Mark, mark))).ToList();
            var suffixed = mark == null || mark.Length == 0 ? null : request.DefaultName + " " + mark;
            if (matches.Count > 0)
            {
                var chosen = matches
                    .OrderBy(i => string.Equals(types[i].Name, request.DefaultName, StringComparison.OrdinalIgnoreCase) ? 0
                        : suffixed != null && string.Equals(types[i].Name, suffixed, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                    .ThenBy(i => mark == null && NormalizeMark(types[i].Mark).Length > 0 ? 1 : 0)
                    .ThenBy(i => types[i].Name, StringComparer.OrdinalIgnoreCase).First();
                return new TypeDecision
                {
                    Step = TypeStep.UseExisting,
                    Index = chosen,
                    TypeName = types[chosen].Name,
                    MatchedBy = mark == null ? "size" : "size_and_mark"
                };
            }

            var names = suffixed == null ? new[] { request.DefaultName } : new[] { request.DefaultName, suffixed };
            foreach (var name in names)
            {
                if (IndexByName(types, name) >= 0) continue;
                var bad = name.IndexOfAny(InvalidNameChars.ToCharArray());
                if (bad >= 0)
                    return new TypeDecision
                    {
                        Step = TypeStep.Refuse,
                        TypeName = name,
                        Error = "INVALID_PARAMS: the new type's name '" + name + "' would contain '" + name[bad] + "', which Revit refuses in a type "
                            + "name (it comes from type_mark); pass type_name."
                    };
                return Create(name);
            }
            var last = IndexByName(types, names[names.Length - 1]);
            var taken = types[last];
            const string advice = "pass type_name to name the new type";
            return SameSize(taken.SizesMm, request.SizesMm)
                ? MarkMismatch(taken, last, familyName, request, advice)
                : SizeMismatch(taken, last, familyName, request, advice);
        }

        private static TypeDecision Create(string name) => new TypeDecision { Step = TypeStep.CreateNew, TypeName = name };

        private static TypeDecision SizeMismatch(TypeFacts type, int index, string familyName, TypeRequest request, string advice)
        {
            var actual = string.Join(", ", request.Labels.Select((label, i) => label + " = " + Show(At(type.SizesMm, i)) + " mm"));
            var requested = string.Join(", ", request.Labels.Select((label, i) => label + " = " + Number(request.SizesMm[i]) + " mm"));
            return new TypeDecision
            {
                Step = TypeStep.Refuse,
                Index = index,
                TypeName = type.Name,
                Error = "TYPE_DIMENSION_MISMATCH: type '" + type.Name + "' of family '" + familyName + "' exists with " + actual + ", not the requested "
                    + requested + ". Existing types are never edited; " + advice + ".",
                Data = Identity(type, familyName, request)
            };
        }

        private static TypeDecision MarkMismatch(TypeFacts type, int index, string familyName, TypeRequest request, string advice) =>
            new TypeDecision
            {
                Step = TypeStep.Refuse,
                Index = index,
                TypeName = type.Name,
                Error = "TYPE_MARK_MISMATCH: type '" + type.Name + "' of family '" + familyName + "' exists with "
                    + (NormalizeMark(type.Mark).Length == 0 ? "no Type Mark" : "Type Mark '" + NormalizeMark(type.Mark) + "'") + ", not '"
                    + NormalizeMark(request.Mark) + "'. Existing types are never edited (setting a Type Mark is an edit); " + advice + ".",
                Data = Identity(type, familyName, request)
            };

        private static JObject Identity(TypeFacts type, string familyName, TypeRequest request) => new JObject
        {
            ["type_name"] = type.Name,
            ["family_name"] = familyName,
            ["actual_mm"] = new JObject(request.Labels.Select((label, i) => new JProperty(label, At(type.SizesMm, i)))),
            ["requested_mm"] = new JObject(request.Labels.Select((label, i) => new JProperty(label, request.SizesMm[i]))),
            ["actual_type_mark"] = NormalizeMark(type.Mark),
            ["requested_type_mark"] = request.Mark == null ? null : NormalizeMark(request.Mark)
        };

        private static double? At(double?[] sizes, int i) => sizes != null && i < sizes.Length ? sizes[i] : null;

        private static string Show(double? mm) => mm.HasValue ? Number(mm.Value) : "?";
    }

    /// <summary>One loaded family of the category as the choice sees it.</summary>
    public sealed class FamilyFacts
    {
        public string Name { get; set; }
        public IList<TypeFacts> Types { get; set; } = new List<TypeFacts>();

        /// <summary>Why its types' size cannot be read (no width/height type parameters); null when it can.</summary>
        public string ReadProblem { get; set; }

        /// <summary>Why a new type cannot be given the size or mark (e.g. Width is computed by a formula); null when it can.</summary>
        public string WriteProblem { get; set; }
    }

    public sealed class FamilyRanking
    {
        public FamilyFacts Family { get; internal set; }
        public int Score { get; internal set; }
        public List<string> Reasons { get; } = new List<string>();
        public bool Eligible => Rejection == null;
        public string Rejection { get; internal set; }

        /// <summary>The type step this family would take (null when it cannot carry the type).</summary>
        public TypeDecision Type { get; internal set; }

        public JObject ToJson() => new JObject
        {
            ["family_name"] = Family.Name,
            ["eligible"] = Eligible,
            ["score"] = Eligible ? (JToken)Score : null,
            ["reasons"] = new JArray(Reasons),
            ["type_step"] = Type == null ? null : Type.Step == TypeStep.UseExisting ? "existing" : "duplicated",
            ["type_name"] = Type?.TypeName,
            ["rejected_because"] = Rejection
        };
    }

    public sealed class FamilyChoice
    {
        /// <summary>The chosen family's ranking; null when none was chosen (see Error).</summary>
        public FamilyRanking Chosen { get; internal set; }

        public string Reason { get; internal set; }

        /// <summary>Every family looked at: eligible ones best first, then the rejected ones.</summary>
        public List<FamilyRanking> Ranked { get; internal set; } = new List<FamilyRanking>();

        /// <summary>"FAMILY_CHOICE_NEEDED: ..." or "FAMILY_NOT_FOUND: ..." when nothing was chosen.</summary>
        public string Error { get; internal set; }

        public string Code => Error == null ? null : Error.Substring(0, Math.Max(0, Error.IndexOf(':')));

        /// <summary>Every family but the chosen one, as wire JSON.</summary>
        public JArray Alternatives() => new JArray(Ranked.Where(f => f != Chosen).Select(f => f.ToJson()));
    }

    public static class FamilyChoiceRule
    {
        /// <summary>Doors up to this width are single leaf; wider ones double leaf.</summary>
        public const double SingleLeafMaxMm = 1200;

        /// <summary>Points for each hint word a family names: more than any name signal, so the caller's words win.</summary>
        public const int HintBonus = 6;

        private static readonly string[] ExteriorHints = { "exterior", "external", "entrance", "entry", "main", "front" };
        private static readonly string[] InteriorHints = { "interior", "internal" };
        private static readonly string[] Generic = { "door", "doors", "window", "windows", "mm", "x" };
        private static readonly string[] ExteriorWords = { "exterior", "external", "entrance", "entry" };
        private static readonly string[] InteriorWords = { "flush", "passage", "interior", "internal" };

        private static readonly string[] DoorSpecial =
        {
            "sliding", "slider", "bifold", "bi-fold", "folding", "pocket", "overhead", "garage", "revolving", "rolling", "roller", "shutter",
            "curtain", "opening", "cased", "louver", "louvre", "pivot", "storefront"
        };

        private static readonly string[] WindowPreferred = { "fixed", "casement" };

        private static readonly string[] WindowSpecial =
            { "skylight", "roof", "dormer", "curtain", "bay", "louver", "louvre", "opening", "storefront", "clerestory" };

        /// <summary>Scores every family and chooses one, or says why it cannot.</summary>
        public static FamilyChoice Choose(OpeningKind kind, TypeRequest request, string hint, IList<FamilyFacts> families)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var noun = kind == OpeningKind.Door ? "door" : "window";
            var size = TypeMatchRule.Number(request.SizesMm[0]) + " x " + TypeMatchRule.Number(request.SizesMm[1]) + " mm";
            var hints = Words(hint);
            var rankings = (families ?? new List<FamilyFacts>()).Select(f => Rank(kind, request, hints, f)).ToList();
            var eligible = rankings.Where(f => f.Eligible).OrderByDescending(f => f.Score)
                .ThenBy(f => f.Family.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var choice = new FamilyChoice
            {
                Ranked = eligible.Concat(rankings.Where(f => !f.Eligible).OrderBy(f => f.Family.Name, StringComparer.OrdinalIgnoreCase)).ToList()
            };
            if (eligible.Count == 0)
            {
                choice.Error = "FAMILY_NOT_FOUND: no loaded " + noun + " family can carry a " + size + " type"
                    + (rankings.Count == 0 ? " (none is loaded)." : "; see candidates for why each was rejected.");
                return choice;
            }
            var best = eligible[0];
            var tied = eligible.Where(f => f.Score == best.Score).ToList();
            if (best.Score < 0)
            {
                choice.Error = "FAMILY_CHOICE_NEEDED: no loaded " + noun + " family looks right for a " + size + " " + noun + " (best: '"
                    + best.Family.Name + "', " + string.Join("; ", best.Reasons) + "). Ask the user which family to use and pass family_name.";
                return choice;
            }
            if (tied.Count > 1)
            {
                choice.Error = "FAMILY_CHOICE_NEEDED: " + tied.Count + " loaded " + noun + " families fit a " + size + " " + noun + " equally well ("
                    + string.Join(", ", tied.Select(f => "'" + f.Family.Name + "'")) + ", score " + best.Score + "). Ask the user which to use and "
                    + "pass family_name (or a hint word found in one of their names).";
                return choice;
            }
            choice.Chosen = best;
            var why = best.Reasons.Count == 0 ? "no name or type signals either way" : string.Join("; ", best.Reasons);
            var next = eligible.Count > 1 ? "; next best '" + eligible[1].Family.Name + "' scored " + eligible[1].Score
                : "; it is the only loaded " + noun + " family that can carry this type";
            choice.Reason = "'" + best.Family.Name + "' scored " + best.Score + " (" + why + ")" + next + ".";
            return choice;
        }

        private static FamilyRanking Rank(OpeningKind kind, TypeRequest request, List<string> hints, FamilyFacts family)
        {
            var ranking = new FamilyRanking { Family = family };
            var types = family.Types ?? new List<TypeFacts>();
            if (family.ReadProblem != null)
            {
                ranking.Rejection = family.ReadProblem;
                return ranking;
            }
            var decision = TypeMatchRule.Decide(types, family.Name, request);
            ranking.Type = decision;
            if (decision.Step == TypeStep.Refuse)
            {
                ranking.Rejection = decision.Error;
                return ranking;
            }
            if (decision.Step == TypeStep.CreateNew && family.WriteProblem != null)
            {
                ranking.Rejection = "it has no type of this size and a new one cannot be made: " + family.WriteProblem;
                return ranking;
            }

            var name = family.Name.ToLowerInvariant();
            var typeNames = string.Join(" | ", types.Select(t => t.Name ?? "")).ToLowerInvariant();
            var both = name + " | " + typeNames;
            var hinted = new HashSet<string>(hints.Where(h => Has(both, h)));

            if (kind == OpeningKind.Door)
            {
                var wantDouble = request.SizesMm[0] > SingleLeafMaxMm;
                var single = Has(name, "single");
                var dbl = Has(name, "double");
                var where = "its name";
                if (!single && !dbl)
                {
                    single = Has(typeNames, "single");
                    dbl = Has(typeNames, "double");
                    where = "its type names";
                }
                var width = TypeMatchRule.Number(request.SizesMm[0]) + " mm";
                if (single != dbl)
                {
                    if (dbl == wantDouble) Add(ranking, 3, (dbl ? "double" : "single") + " leaf by " + where + ", as a " + width + " wide door is");
                    else Add(ranking, -5, (dbl ? "double" : "single") + " leaf by " + where + ", but a " + width + " wide door is "
                        + (wantDouble ? "double" : "single") + " leaf (double above " + TypeMatchRule.Number(SingleLeafMaxMm) + " mm)");
                }
                var special = DoorSpecial.FirstOrDefault(w => Has(name, w));
                if (special != null && !hinted.Contains(special)) Add(ranking, -4, "special door kind '" + special + "' (name it in hint to prefer it)");

                var exteriorWanted = hints.Any(h => ExteriorHints.Contains(h));
                var functions = types.Select(t => t.Function).Where(f => f != null).ToList();
                var looksExterior = ExteriorWords.Any(w => Has(both, w)) || functions.Contains("exterior");
                var looksInterior = InteriorWords.Any(w => Has(both, w)) || functions.Contains("interior");
                if (exteriorWanted)
                {
                    if (looksExterior) Add(ranking, 2, "exterior kind (Exterior/Entrance name or Function), as hint asks");
                }
                else if (looksInterior) Add(ranking, 2, "interior kind (Flush/Passage/Interior name or Function)");
                else if (looksExterior) Add(ranking, -2, "exterior kind; pass hint \"exterior\" for an entrance door");
            }
            else
            {
                var preferred = WindowPreferred.FirstOrDefault(w => Has(both, w));
                if (preferred != null) Add(ranking, 2, "'" + preferred + "' window (fixed/casement preferred)");
                var special = WindowSpecial.FirstOrDefault(w => Has(name, w));
                if (special != null && !hinted.Contains(special)) Add(ranking, -4, "special window kind '" + special + "' (name it in hint to prefer it)");
            }

            foreach (var word in hinted.Where(h => !ExteriorHints.Contains(h) && !InteriorHints.Contains(h)))
                Add(ranking, HintBonus, "matches hint '" + word + "'");

            var sameSize = types.Where(t => TypeMatchRule.SameSize(t.SizesMm, request.SizesMm)).ToList();
            if (sameSize.Count > 0)
            {
                Add(ranking, 2, "already has a type of this size ('" + sameSize[0].Name + "')");
                var mark = request.Mark == null ? null : TypeMatchRule.NormalizeMark(request.Mark);
                var marked = mark == null ? null : sameSize.FirstOrDefault(t => TypeMatchRule.SameMark(t.Mark, mark));
                if (marked != null) Add(ranking, 2, "its type '" + marked.Name + "' already carries Type Mark '" + mark + "'");
            }
            return ranking;
        }

        private static void Add(FamilyRanking ranking, int points, string reason)
        {
            ranking.Score += points;
            ranking.Reasons.Add((points > 0 ? "+" : "") + points.ToString(CultureInfo.InvariantCulture) + " " + reason);
        }

        private static bool Has(string text, string word) => text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>The hint's words (lower case, two letters or more), without generic words such as door or window.</summary>
        public static List<string> Words(string hint) =>
            System.Text.RegularExpressions.Regex.Split((hint ?? "").ToLowerInvariant(), "[^a-z0-9-]+")
                .Where(w => w.Length >= 2 && !Generic.Contains(w)).Distinct().ToList();
    }
}
