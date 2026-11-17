// New in Plinth: ensure_family_type for doors and windows. Imported drawings name openings by mark and size (D = 900 x
// 2100, W1 = 1000 x 1200), so the build needs a type with exactly that width/height and Type Mark. The decisions are
// Revit-free (addin/src/Core/Families/OpeningTypeRules.cs); this file reads the facts from the project and applies them:
//   - width/height are TYPE parameters found by built-in DOOR_WIDTH/WINDOW_WIDTH, then FAMILY_WIDTH_PARAM, then the names
//     Width / Rough Width (heights alike). A family whose size is an instance parameter cannot carry a size per type;
//     door and window families are never edited or adapted, so such a family is rejected with the reason;
//   - family_name omitted: the loaded families of the category are scored (FamilyChoiceRule) and the unique best is
//     used; a tie or a poor best is FAMILY_CHOICE_NEEDED with the candidates;
//   - there is no recipe to build a door or window family: nothing found is FAMILY_NOT_FOUND with can_create false.
// Duplicate before edit: an existing type is only ever read. Width, height and Type Mark are set on a type this call
// made; a type that matches the size but carries another mark is left alone and a new type carries the mark.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Plinth.Revit.Bridge;
using Plinth.Revit.Core.Families;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Families
{
    internal sealed class DoorRecipe : FamilyRecipe
    {
        private static readonly SectionDimension Width = new SectionDimension("width_mm", "width", new[] { "Width", "Rough Width" },
            new[] { BuiltInParameter.DOOR_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM });

        private static readonly SectionDimension Height = new SectionDimension("height_mm", "height", new[] { "Height", "Rough Height" },
            new[] { BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM });

        public override string Key => "door";
        public override BuiltInCategory Category => BuiltInCategory.OST_Doors;
        public override string TemplateFileName => null;
        public override IReadOnlyList<SectionDimension> Dimensions => new[] { Width, Height };
        public override OpeningKind? Opening => OpeningKind.Door;
        public override string[][] RelatedNameWords => new[] { new[] { "door" } };
        public override string[] CategoryWords => new[] { "door" };

        internal override BuiltGeometry Build(FamilyBuilder fb, FamilyParameter pb, FamilyParameter ph, double b, double h) =>
            throw new FamilyBuildException("choose a recipe", "Plinth has no recipe that builds door families");
    }

    internal sealed class WindowRecipe : FamilyRecipe
    {
        private static readonly SectionDimension Width = new SectionDimension("width_mm", "width", new[] { "Width", "Rough Width" },
            new[] { BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM });

        private static readonly SectionDimension Height = new SectionDimension("height_mm", "height", new[] { "Height", "Rough Height" },
            new[] { BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM });

        public override string Key => "window";
        public override BuiltInCategory Category => BuiltInCategory.OST_Windows;
        public override string TemplateFileName => null;
        public override IReadOnlyList<SectionDimension> Dimensions => new[] { Width, Height };
        public override OpeningKind? Opening => OpeningKind.Window;
        public override string[][] RelatedNameWords => new[] { new[] { "window" } };
        public override string[] CategoryWords => new[] { "window" };

        internal override BuiltGeometry Build(FamilyBuilder fb, FamilyParameter pb, FamilyParameter ph, double b, double h) =>
            throw new FamilyBuildException("choose a recipe", "Plinth has no recipe that builds window families");
    }

    internal static class OpeningFamilies
    {
        private const int MaxCandidates = 10;

        /// <summary>The request as the Revit-free rules see it.</summary>
        public static TypeRequest Request(EnsureRequest r) => new TypeRequest
        {
            ExplicitName = r.TypeNameGiven ? r.TypeName : null,
            DefaultName = r.DefaultTypeName,
            SizesMm = r.Recipe.Dimensions.Select(d => r.Mm[d]).ToArray(),
            Labels = r.Recipe.Dimensions.Select(d => d.Parameter).ToArray(),
            Mark = r.TypeMark
        };

        /// <summary>A type's sizes (null where unreadable), Type Mark and Function.</summary>
        public static TypeFacts Facts(EnsureRequest r, FamilySymbol symbol)
        {
            var sizes = new double?[r.Recipe.Dimensions.Count];
            if (EnsureFamilyTypeHandler.TryDimensionParameters(r, symbol, out var parameters, out _))
                for (var i = 0; i < sizes.Length; i++) sizes[i] = Mm.FromFeet(parameters[r.Recipe.Dimensions[i]].AsDouble(), 3);
            return new TypeFacts { Name = symbol.Name, SizesMm = sizes, Mark = MarkOf(symbol), Function = FunctionOf(symbol) };
        }

        public static string MarkOf(Element type) => type.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString() ?? "";

        /// <summary>"interior" or "exterior" from the type's Function parameter (0 Interior, 1 Exterior), else null.</summary>
        private static string FunctionOf(Element type)
        {
            var parameter = type.get_Parameter(BuiltInParameter.FUNCTION_PARAM);
            if (parameter == null || parameter.StorageType != StorageType.Integer || !parameter.HasValue) return null;
            switch (parameter.AsInteger())
            {
                case 0: return "interior";
                case 1: return "exterior";
                default: return null;
            }
        }

        /// <summary>Family facts for the choice: its types, and whether their size can be read and given to a new type.</summary>
        private static FamilyFacts Facts(EnsureRequest r, Family family)
        {
            var types = EnsureFamilyTypeHandler.TypesOf(family);
            var facts = new FamilyFacts { Name = family.Name, Types = types.Select(t => Facts(r, t)).ToList() };
            if (types.Count == 0) facts.ReadProblem = "it has no types";
            else if (!EnsureFamilyTypeHandler.TryDimensionParameters(r, types[0], out _, out var failure)) facts.ReadProblem = failure.Error;
            else
            {
                var source = types[Math.Max(0, TypeMatchRule.ClosestIndex(facts.Types, Request(r).SizesMm))];
                if (!EnsureFamilyTypeHandler.TryCanCreate(r, source, out failure)) facts.WriteProblem = failure.Error;
            }
            return facts;
        }

        /// <summary>family_name omitted: choose among the loaded families of the category, then ensure the type in the chosen one.</summary>
        public static CommandResult EnsureChosen(EnsureRequest r, List<FamilyFileSearch.Root> roots, string year)
        {
            var families = new FilteredElementCollector(r.Doc).OfClass(typeof(Family)).Cast<Family>()
                .Where(f => !f.IsInPlace && f.FamilyCategory != null && f.FamilyCategory.BuiltInCategory == r.Recipe.Category)
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var facts = families.Select(f => Facts(r, f)).ToList();
            var choice = FamilyChoiceRule.Choose(r.Recipe.Opening.Value, Request(r), r.Hint, facts);
            if (choice.Chosen == null)
            {
                if (choice.Code == "FAMILY_NOT_FOUND") return NotFound(r, roots, year, choice);
                return CommandResult.Fail(choice.Error + EnsureFamilyTypeHandler.Unchanged, new JObject
                {
                    ["candidates"] = new JArray(choice.Ranked.Select(f => f.ToJson())),
                    ["dry_run"] = r.DryRun
                });
            }
            var family = families[facts.IndexOf(choice.Chosen.Family)];
            r.FamilyName = family.Name;
            r.ChosenReason = choice.Reason;
            r.Alternatives = choice.Alternatives();
            return EnsureFamilyTypeHandler.EnsureInLoaded(r, family, "project", null);
        }

        /// <summary>ASK: nothing fits and nothing can be built. Lists loaded families of the category and family files that look like it.</summary>
        public static CommandResult NotFound(EnsureRequest r, List<FamilyFileSearch.Root> roots, string year, FamilyChoice choice)
        {
            var noun = r.Recipe.Key;
            var named = r.FamilyName.Length > 0;
            JArray loaded;
            if (choice != null) loaded = new JArray(choice.Ranked.Select(f => With(f.ToJson(), "project")));
            else
                loaded = new JArray(new FilteredElementCollector(r.Doc).OfClass(typeof(Family)).Cast<Family>()
                    .Where(f => !f.IsInPlace && f.FamilyCategory != null && f.FamilyCategory.BuiltInCategory == r.Recipe.Category)
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Take(MaxCandidates)
                    .Select(f => new JObject { ["family_name"] = f.Name, ["source"] = "project" }));

            var words = named ? EnsureFamilyTypeHandler.Words(r.FamilyName).ToList() : new List<string>();
            int Score(FamilyFileSearch.Hit h) => words.Count(w => h.FamilyName.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
            var onDisk = FamilyFileSearch.ListAll(roots).Select((hit, index) => new { hit, index })
                .Where(x => Score(x.hit) > 0 || r.Recipe.LooksLikeCategory(x.hit.FileName, x.hit.Folder))
                .OrderByDescending(x => Score(x.hit)).ThenBy(x => x.index).Take(MaxCandidates)
                .Select(x => With(x.hit.ToJson(), "disk"));

            var what = named
                ? "no " + noun + " family named '" + r.FamilyName + "' is loaded or was found in " + EnsureFamilyTypeHandler.Searched(roots, year)
                : choice.Error.Substring(choice.Error.IndexOf(':') + 2).TrimEnd('.');
            return CommandResult.Fail("FAMILY_NOT_FOUND: " + what + ". Plinth has no recipe that builds " + noun + " families (can_create is false"
                + (r.AllowCreate ? ", so allow_create does not apply" : "") + "). Ask the user which family to use: pass family_name of a loaded "
                + "family, or of a family file (give its folder in search_folders); candidates lists both." + EnsureFamilyTypeHandler.Unchanged,
                new JObject
                {
                    ["searched_roots"] = new JArray(roots.Select(root => root.ToJson())),
                    ["candidates"] = new JArray(loaded.Concat(onDisk)),
                    ["can_create"] = false,
                    ["template"] = null,
                    ["dry_run"] = r.DryRun
                });
        }

        /// <summary>Types of the category, other than except, already carrying this Type Mark.</summary>
        public static List<FamilySymbol> MarkHolders(Document doc, BuiltInCategory category, string mark, ElementId except)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(category).Cast<FamilySymbol>()
                .Where(s => (except == null || s.Id != except) && TypeMatchRule.NormalizeMark(MarkOf(s)).Length > 0 && TypeMatchRule.SameMark(MarkOf(s), mark))
                .OrderBy(s => s.FamilyName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static JObject With(JObject json, string source)
        {
            json["source"] = source;
            return json;
        }
    }
}
