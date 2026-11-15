// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Mep/CreatePipeHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: level (by name) and pipe type (by id or name) are required; without a system type the pipe
// must inherit one from an open piping connector at the start, otherwise it is an error listing the piping
// system types (rvt-mcp fell back to the first PipeType, the first PipingSystemType and the nearest level);
// the start-connector search only looks at elements whose bounding box is near the start point (rvt-mcp scanned
// every MEP curve and family instance); an ambiguous start is a failure carrying the candidates; a diameter
// the type cannot take rolls back; errors are "CODE: message".

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Mep
{
    public sealed class CreatePipeHandler : IRevitCommand
    {
        private static readonly double StartTolerance = Mm.ToFeet(1.0);
        private const double DiameterToleranceMm = 0.01;

        public string Name => "create_pipe";
        public string Description =>
            "Create a pipe between two points (mm, internal coordinates; z is the absolute elevation). Required: level name and pipe type "
            + "(pipe_type_id or pipe_type_name). Give a piping system type (system_type_id or system_type_name) for an independent pipe, "
            + "or omit it to continue from the one open piping connector within 1 mm of the start (start_element_id and start_connector_id "
            + "pick it when several match): the pipe then inherits that system and size and is connected. Optional diameter in mm.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + MepHelpers.EndpointProperties + @",
            ""level"":{""type"":""string""},
            ""pipe_type_id"":{""type"":""integer""},
            ""pipe_type_name"":{""type"":""string""},
            ""system_type_id"":{""type"":""integer""},
            ""system_type_name"":{""type"":""string""},
            ""start_element_id"":{""type"":""integer""},
            ""start_connector_id"":{""type"":""integer""},
            ""diameter"":{""type"":""number""}},
            ""required"":[""start_x"",""start_y"",""start_z"",""end_x"",""end_y"",""end_z"",""level""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var diameterMm = p.Value<double?>("diameter");
            if (diameterMm.HasValue && diameterMm.Value <= 0) return CommandResult.Fail("INVALID_PARAMS: diameter must be more than 0 mm.");
            var systemTypeId = p.Value<long?>("system_type_id");
            var systemTypeName = p.Value<string>("system_type_name");
            var hasSystemType = systemTypeId.HasValue || !string.IsNullOrEmpty(systemTypeName);
            var startElementId = p.Value<long?>("start_element_id");
            var startConnectorId = p.Value<int?>("start_connector_id");
            if (startConnectorId.HasValue && !startElementId.HasValue)
                return CommandResult.Fail("INVALID_PARAMS: start_connector_id needs start_element_id.");
            if (hasSystemType && startElementId.HasValue)
                return CommandResult.Fail("INVALID_PARAMS: omit the system type when choosing a start connector to continue from.");

            if (!MepHelpers.TryEndpoints(app.Application, p, out var start, out var end, out var failure)) return failure;
            if (!Lookup.TryLevel(doc, p.Value<string>("level"), out var level, out failure)) return failure;
            if (!MepHelpers.TryType<PipeType>(doc, p.Value<long?>("pipe_type_id"), p.Value<string>("pipe_type_name"), "pipe",
                    "pipe_type_id", "pipe_type_name", out var pipeType, out failure)) return failure;

            PipingSystemType systemType = null;
            Connector startConnector = null;
            if (hasSystemType)
            {
                if (!MepHelpers.TryType(doc, systemTypeId, systemTypeName, "piping system", "system_type_id", "system_type_name",
                        out systemType, out failure)) return failure;
            }
            else
            {
                if (startElementId.HasValue && doc.GetElement(RevitCompat.ToElementId(startElementId.Value)) == null)
                    return CommandResult.Fail("ELEMENT_NOT_FOUND: start element " + startElementId.Value + " is not in this document.");
                var candidates = FindStartConnectors(doc, start, startElementId, startConnectorId);
                if (candidates.Count > 1)
                    return CommandResult.Fail("AMBIGUOUS_START_CONNECTOR: several open piping connectors lie at the start; pass start_element_id "
                        + "and start_connector_id.", new
                        {
                            candidates = candidates.Select(c => new { element_id = RevitCompat.GetId(c.Owner.Id), connector_id = c.Id }).ToArray()
                        });
                if (candidates.Count == 0)
                    return CommandResult.Fail(startElementId.HasValue
                            ? "START_CONNECTOR_NOT_FOUND: the chosen element has no open piping end connector within 1 mm of the start point."
                            : "SYSTEM_TYPE_REQUIRED: no open piping connector lies at the start point; pass system_type_id or system_type_name.",
                        new JObject { ["valid_piping_system_types"] = MepHelpers.Options(MepHelpers.TypesOf<PipingSystemType>(doc)) });
                startConnector = candidates[0];
                MepConnectionSystemType.Read(startConnector); // Throws on an unreadable system rather than guessing.
                var connectorDiameterMm = Mm.FromFeet(startConnector.Radius * 2, 3);
                if (diameterMm.HasValue && Math.Abs(diameterMm.Value - connectorDiameterMm) > DiameterToleranceMm)
                    return CommandResult.Fail("DIAMETER_MISMATCH: the requested diameter differs from the start connector's (" + connectorDiameterMm
                        + " mm). Omit diameter to inherit it, or add a transition.");
            }

            using (var tx = new Transaction(doc, "B2M: Create Pipe"))
            {
                tx.Start();
                try
                {
                    var pipe = startConnector == null
                        ? Pipe.Create(doc, systemType.Id, pipeType.Id, level.Id, start, end)
                        : Pipe.Create(doc, pipeType.Id, level.Id, startConnector, end);
                    if (pipe == null) throw new InvalidOperationException("Revit returned no pipe.");
                    if (startConnector == null && diameterMm.HasValue)
                        MepHelpers.SetLengthMm(pipe, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, diameterMm.Value, "diameter");

                    doc.Regenerate();
                    var actualSystemType = doc.GetElement(pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM).AsElementId()) as PipingSystemType;
                    if (actualSystemType == null) throw new InvalidOperationException("cannot read the new pipe's system type.");
                    if (startConnector != null && !startConnector.IsConnected)
                        throw new InvalidOperationException("the new pipe did not connect to the start connector.");

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    return CommandResult.Ok(new
                    {
                        pipe_id = RevitCompat.GetId(pipe.Id),
                        pipe_type = pipeType.Name,
                        pipe_type_id = RevitCompat.GetId(pipeType.Id),
                        system_type = actualSystemType.Name,
                        system_type_id = RevitCompat.GetId(actualSystemType.Id),
                        system_type_source = startConnector == null ? "explicit" : "start_connector",
                        connected_to_start = startConnector != null,
                        start_element_id = startConnector == null ? (long?)null : RevitCompat.GetId(startConnector.Owner.Id),
                        start_connector_id = startConnector?.Id,
                        level = level.Name,
                        length_mm = MepHelpers.ReadLengthMm(pipe, BuiltInParameter.CURVE_ELEM_LENGTH),
                        diameter_mm = MepHelpers.ReadLengthMm(pipe, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the pipe: " + ex.Message + " Nothing was created.");
                }
            }
        }

        /// <summary>Open piping End connectors within 1 mm of the start, looking only at elements near it.</summary>
        private static List<Connector> FindStartConnectors(Document doc, XYZ start, long? elementId, int? connectorId)
        {
            IEnumerable<Element> owners;
            if (elementId.HasValue)
            {
                owners = new[] { doc.GetElement(RevitCompat.ToElementId(elementId.Value)) };
            }
            else
            {
                var margin = new XYZ(Mm.ToFeet(10), Mm.ToFeet(10), Mm.ToFeet(10));
                var near = new BoundingBoxIntersectsFilter(new Autodesk.Revit.DB.Outline(start - margin, start + margin));
                owners = new FilteredElementCollector(doc).OfClass(typeof(MEPCurve)).WherePasses(near).Cast<Element>()
                    .Concat(new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WherePasses(near).Cast<Element>());
            }
            var matches = new List<Connector>();
            foreach (var owner in owners)
            {
                var manager = MepHelpers.GetConnectorManager(owner);
                if (manager == null) continue;
                foreach (Connector connector in manager.Connectors)
                {
                    if (connectorId.HasValue && connector.Id != connectorId.Value) continue;
                    if (connector.ConnectorType == ConnectorType.End && connector.Domain == Domain.DomainPiping
                        && !connector.IsConnected && connector.Origin.DistanceTo(start) <= StartTolerance)
                        matches.Add(connector);
                }
            }
            return matches;
        }
    }
}
