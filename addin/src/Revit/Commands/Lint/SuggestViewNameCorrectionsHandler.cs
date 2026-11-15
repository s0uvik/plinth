// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Lint/SuggestViewNameCorrectionsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: suggestions fill the pattern's {NN}/{Name} slots from the current name (rvt-mcp returned the pattern
// itself, e.g. "L{NN}-{Name}"); a name that cannot be mapped gets suggested null with a reason; a suggestion that
// another view already uses is flagged; an unknown profile is an error listing the available ones (rvt-mcp returned
// success with an error field); the 50-suggestion cap is a parameter (max_suggestions, at most 200). Nothing is renamed.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Lint
{
    using View = Autodesk.Revit.DB.View;

    public sealed class SuggestViewNameCorrectionsHandler : IRevitCommand
    {
        public string Name => "suggest_view_name_corrections";
        public string Description => "Propose new names for views that nearly follow the project's dominant naming pattern, or the viewName rule "
            + "of a firm profile (profile id from %LOCALAPPDATA%\\B2M\\firm-profiles). Only proposes; renames nothing.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""profile"":{""type"":""string""},
            ""max_suggestions"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            var cap = p.Value<int?>("max_suggestions") ?? 50;
            if (cap < 1 || cap > 200) return CommandResult.Fail("INVALID_PARAMS: max_suggestions must be between 1 and 200.");

            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
            var byName = views.Where(v => v.CanBePrinted).GroupBy(v => v.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(v => RevitCompat.GetId(v.Id)).OrderBy(id => id).ToList(), StringComparer.Ordinal);
            var taken = new HashSet<string>(views.Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
            var analysis = NamingLint.Analyze(byName.Keys);

            string pattern, source;
            var profileId = p.Value<string>("profile");
            if (!string.IsNullOrEmpty(profileId))
            {
                var profiles = NamingLint.LoadProfiles(out var problems);
                var profile = profiles.FirstOrDefault(x => string.Equals(x.Id, profileId, StringComparison.OrdinalIgnoreCase));
                if (profile == null)
                    return CommandResult.Fail("PROFILE_NOT_FOUND: no firm profile '" + profileId + "' in " + NamingLint.ProfileFolder + ". Omit profile to use the project's own pattern.",
                        new JObject { ["available"] = new JArray(profiles.Select(x => x.Id)), ["problems"] = new JArray(problems) });
                if (string.IsNullOrEmpty(profile.Rules?.ViewName))
                    return CommandResult.Fail("PROFILE_HAS_NO_VIEW_RULE: profile '" + profile.Id + "' has no rules.viewName.");
                pattern = profile.Rules.ViewName;
                source = "profile:" + profile.Id;
            }
            else
            {
                pattern = analysis.Dominant;
                source = "inferred";
            }

            var suggestions = new JArray();
            if (!string.IsNullOrEmpty(pattern))
            {
                // With a profile, every printable view not matching its rule is a candidate; inferred mode uses the outliers.
                var candidates = source == "inferred"
                    ? analysis.Outliers.Select(o => o.name)
                    : byName.Keys.Where(n => NamingLint.Tokenize(n) != pattern).OrderBy(n => n, StringComparer.Ordinal);
                foreach (var name in candidates.Take(cap))
                {
                    var suggested = NamingLint.Suggest(name, pattern);
                    suggestions.Add(new JObject
                    {
                        ["id"] = byName[name][0],
                        ["ids"] = new JArray(byName[name]),
                        ["current"] = name,
                        ["suggested"] = suggested,
                        ["conflicts_with_existing"] = suggested != null && !string.Equals(suggested, name, StringComparison.OrdinalIgnoreCase) && taken.Contains(suggested),
                        ["reason"] = suggested == null
                            ? "the name lacks a number or word that " + pattern + " needs; rename by hand"
                            : (source == "inferred" ? "matches dominant " : "matches profile rule ") + pattern
                    });
                }
            }
            return CommandResult.Ok(new JObject
            {
                ["source"] = source,
                ["pattern"] = pattern,
                ["note"] = string.IsNullOrEmpty(pattern) ? "No pattern covers 50 % of the views, so there is nothing to correct towards." : null,
                ["suggestions"] = suggestions
            });
        }
    }
}
