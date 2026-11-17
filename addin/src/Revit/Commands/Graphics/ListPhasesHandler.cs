// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Documents/ListPhasesHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: moved to the graphics toolset next to set_view_phase and set_element_phase; read errors
// surface instead of being swallowed into a shorter list.

using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Graphics
{
    public sealed class ListPhasesHandler : IRevitCommand
    {
        public string Name => "list_phases";
        public string Description => "List the project phases in sequence order and the phase filters.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var phases = new JArray();
            var index = 0;
            foreach (Phase phase in doc.Phases)
            {
                phases.Add(new JObject
                {
                    ["id"] = RevitCompat.GetId(phase.Id),
                    ["name"] = phase.Name,
                    ["sequence_index"] = index++
                });
            }

            var filters = new JArray(new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>()
                .OrderBy(f => f.Name)
                .Select(f => new JObject { ["id"] = RevitCompat.GetId(f.Id), ["name"] = f.Name }));

            return CommandResult.Ok(new JObject
            {
                ["doc_title"] = doc.Title,
                ["total_phases"] = phases.Count,
                ["phases"] = phases,
                ["total_phase_filters"] = filters.Count,
                ["phase_filters"] = filters
            });
        }
    }
}
