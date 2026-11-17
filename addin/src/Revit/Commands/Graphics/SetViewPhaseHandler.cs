// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Views/SetViewPhaseHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: moved to the graphics toolset; id and name for the same field together are an error;
// a missing phase, phase filter or view lists the valid ones; parameter checks run before the transaction;
// failures are errors with codes (rvt-mcp returned success with an error field).

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Graphics
{
    using View = Autodesk.Revit.DB.View;

    public sealed class SetViewPhaseHandler : IRevitCommand
    {
        public string Name => "set_view_phase";
        public string Description => "Set a view's Phase and/or Phase Filter, each by id or by name. view_id defaults to the active view.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""view_id"":{""type"":""integer""},
            ""phase_id"":{""type"":""integer""},
            ""phase_name"":{""type"":""string""},
            ""phase_filter_id"":{""type"":""integer""},
            ""phase_filter_name"":{""type"":""string""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var phaseId = p.Value<long?>("phase_id");
            var phaseName = p.Value<string>("phase_name");
            var filterId = p.Value<long?>("phase_filter_id");
            var filterName = p.Value<string>("phase_filter_name");
            if (phaseId.HasValue && phaseName != null) return CommandResult.Fail("INVALID_PARAMS: pass phase_id or phase_name, not both.");
            if (filterId.HasValue && filterName != null) return CommandResult.Fail("INVALID_PARAMS: pass phase_filter_id or phase_filter_name, not both.");
            var wantPhase = phaseId.HasValue || phaseName != null;
            var wantFilter = filterId.HasValue || filterName != null;
            if (!wantPhase && !wantFilter)
                return CommandResult.Fail("INVALID_PARAMS: give phase_id or phase_name, and/or phase_filter_id or phase_filter_name.");

            if (!GraphicsHelpers.TryView(doc, p.Value<long?>("view_id"), out var view, out var failure)) return failure;

            Phase phase = null;
            if (wantPhase && !GraphicsHelpers.TryPhase(doc, phaseId, phaseName, "phase", out phase, out failure)) return failure;

            PhaseFilter phaseFilter = null;
            if (wantFilter)
            {
                var all = new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>().OrderBy(f => f.Name).ToList();
                phaseFilter = filterId.HasValue
                    ? all.FirstOrDefault(f => RevitCompat.GetId(f.Id) == filterId.Value)
                    : all.FirstOrDefault(f => string.Equals(f.Name, filterName.Trim(), StringComparison.OrdinalIgnoreCase));
                if (phaseFilter == null)
                    return CommandResult.Fail("PHASE_FILTER_NOT_FOUND: " + (filterId.HasValue ? "no phase filter has id " + filterId.Value : "no phase filter named '" + filterName + "'") + ".",
                        new JObject { ["valid_phase_filters"] = new JArray(all.Select(f => new JObject { ["id"] = RevitCompat.GetId(f.Id), ["name"] = f.Name })) });
            }

            var phaseParam = wantPhase ? view.get_Parameter(BuiltInParameter.VIEW_PHASE) : null;
            var filterParam = wantFilter ? view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER) : null;
            if (wantPhase && (phaseParam == null || phaseParam.IsReadOnly))
                return CommandResult.Fail("PARAMETER_READ_ONLY: view '" + view.Name + "' (" + view.ViewType + ") has no editable Phase"
                    + (view.ViewTemplateId != ElementId.InvalidElementId ? "; its view template may control it" : "") + ".");
            if (wantFilter && (filterParam == null || filterParam.IsReadOnly))
                return CommandResult.Fail("PARAMETER_READ_ONLY: view '" + view.Name + "' (" + view.ViewType + ") has no editable Phase Filter"
                    + (view.ViewTemplateId != ElementId.InvalidElementId ? "; its view template may control it" : "") + ".");

            using (var tx = new Transaction(doc, "Plinth: Set View Phase"))
            {
                tx.Start();
                try
                {
                    if (wantPhase && !phaseParam.Set(phase.Id))
                    {
                        tx.RollBack();
                        return CommandResult.Fail("SET_FAILED: Revit refused phase '" + phase.Name + "' for view '" + view.Name + "'.");
                    }
                    if (wantFilter && !filterParam.Set(phaseFilter.Id))
                    {
                        tx.RollBack();
                        return CommandResult.Fail("SET_FAILED: Revit refused phase filter '" + phaseFilter.Name + "' for view '" + view.Name + "'.");
                    }
                    if (tx.Commit() != TransactionStatus.Committed) return GraphicsHelpers.NotCommitted();
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("SET_FAILED: could not set the phase of view '" + view.Name + "': " + ex.Message);
                }
            }

            var result = GraphicsHelpers.ViewRef(view);
            result["phase"] = Read(doc, view, BuiltInParameter.VIEW_PHASE);
            result["phase_filter"] = Read(doc, view, BuiltInParameter.VIEW_PHASE_FILTER);
            return CommandResult.Ok(result);
        }

        private static JObject Read(Document doc, View view, BuiltInParameter bip)
        {
            var id = view.get_Parameter(bip)?.AsElementId();
            var element = id == null ? null : doc.GetElement(id);
            return element == null ? null : new JObject { ["id"] = RevitCompat.GetId(element.Id), ["name"] = element.Name };
        }
    }
}
