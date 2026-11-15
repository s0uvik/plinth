// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Documents/GetModelWarningsSummaryHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: elementIds limits the summary to warnings that involve those elements, so a run can
// check only what it built; bounds are enforced by the schema checker and here.

using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Lint
{
    public sealed class GetModelWarningsSummaryHandler : IRevitCommand
    {
        public string Name => "get_model_warnings_summary";
        public string Description => "Group the model's warnings by description with counts and example element ids. elementIds limits it to warnings touching those elements.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""include_examples"":{""type"":""boolean""},
            ""max_examples_per_type"":{""type"":""integer""},
            ""max_warning_types"":{""type"":""integer""},
            ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var includeExamples = p.Value<bool?>("include_examples") ?? true;
            var maxExamples = p.Value<int?>("max_examples_per_type") ?? 5;
            var maxTypes = p.Value<int?>("max_warning_types") ?? 200;
            if (maxExamples < 0 || maxExamples > 100) return CommandResult.Fail("INVALID_PARAMS: max_examples_per_type must be 0 to 100.");
            if (maxTypes < 1 || maxTypes > 1000) return CommandResult.Fail("INVALID_PARAMS: max_warning_types must be 1 to 1000.");

            var only = p["elementIds"]?.ToObject<long[]>();
            var filter = only == null ? null : new HashSet<long>(only);
            var warnings = doc.GetWarnings()
                .Where(w => filter == null || w.GetFailingElements().Concat(w.GetAdditionalElements()).Any(id => filter.Contains(RevitCompat.GetId(id))))
                .ToList();

            var grouped = warnings
                .GroupBy(w => w.GetDescriptionText())
                .OrderByDescending(g => g.Count())
                .Take(maxTypes)
                .Select(g => new
                {
                    description = g.Key,
                    count = g.Count(),
                    severity = g.First().GetSeverity().ToString(),
                    examples = includeExamples
                        ? g.Take(maxExamples).Select(w => new { failing_element_ids = w.GetFailingElements().Select(RevitCompat.GetId).ToList() }).ToList()
                        : null
                })
                .ToList();
            var unique = warnings.Select(w => w.GetDescriptionText()).Distinct().Count();

            return CommandResult.Ok(new
            {
                total_warnings = warnings.Count,
                unique_descriptions = unique,
                returned_warning_types = grouped.Count,
                truncated = grouped.Count < unique,
                warnings = grouped
            });
        }
    }
}
