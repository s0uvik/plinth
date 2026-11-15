// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Lint/DetectFirmProfileHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: profiles load from %LOCALAPPDATA%\B2M\firm-profiles (see NamingLint); the result names that folder,
// how many profiles loaded and any files that could not be read; the sheet prefix ignores placeholder sheets.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Lint
{
    using View = Autodesk.Revit.DB.View;

    public sealed class DetectFirmProfileHandler : IRevitCommand
    {
        public string Name => "detect_firm_profile";
        public string Description => "Fingerprint the project's naming (dominant view-name pattern, sheet-number prefix, level-name pattern) and match it "
            + "against the firm profiles in %LOCALAPPDATA%\\B2M\\firm-profiles. library_match is null when none matches.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var viewDominant = NamingLint.Analyze(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.CanBePrinted).Select(v => v.Name)).Dominant;
            var sheetPrefix = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder && !string.IsNullOrEmpty(s.SheetNumber))
                .Select(s => new string(s.SheetNumber.TakeWhile(char.IsLetter).ToArray()))
                .Where(x => x.Length > 0)
                .GroupBy(x => x).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .FirstOrDefault()?.Key;
            var levelPattern = NamingLint.Analyze(new FilteredElementCollector(doc).OfClass(typeof(Level)).Select(l => l.Name)).Dominant;

            var profiles = NamingLint.LoadProfiles(out var problems);
            var match = NamingLint.Match(profiles, sheetPrefix, viewDominant, levelPattern);
            return CommandResult.Ok(new JObject
            {
                ["project_pattern"] = new JObject
                {
                    ["view_dominant"] = viewDominant,
                    ["sheet_prefix"] = sheetPrefix,
                    ["level_pattern"] = levelPattern
                },
                ["library_match"] = match == null ? null : new JObject
                {
                    ["profile_id"] = match.Value.profile.Id,
                    ["profile_name"] = match.Value.profile.Name,
                    ["confidence"] = Math.Round(match.Value.confidence, 3),
                    ["matched_hints"] = new JArray(match.Value.hints)
                },
                ["library"] = new JObject
                {
                    ["folder"] = NamingLint.ProfileFolder,
                    ["profile_count"] = profiles.Count,
                    ["problems"] = new JArray(problems)
                }
            });
        }
    }
}
