// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Structural/CreateStructuralBeamHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: level (by name) and framing type (type_id, or type_name + family_name) are required and a miss lists
// the options (rvt-mcp used the first framing type and the lowest level); start_z/end_z default to the level elevation
// (rvt-mcp used 0); usage "joist" dropped (rvt-mcp silently created a beam); lines shorter than Revit allows are refused;
// the response reads the placed curve back.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Structural
{
    public sealed class CreateStructuralBeamHandler : IRevitCommand
    {
        public string Name => "create_structural_beam";
        public string Description =>
            "Create a structural beam or brace between two points in mm on a named reference level; start_z/end_z are absolute "
            + "elevations (default the level). Type by type_id or type_name (+ family_name). usage: beam (default) or brace.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""type_id"":{""type"":""integer""},
            ""type_name"":{""type"":""string""},
            ""family_name"":{""type"":""string""},
            ""level"":{""type"":""string""},
            ""start_x"":{""type"":""number""},""start_y"":{""type"":""number""},""start_z"":{""type"":""number""},
            ""end_x"":{""type"":""number""},""end_y"":{""type"":""number""},""end_z"":{""type"":""number""},
            ""usage"":{""type"":""string"",""enum"":[""beam"",""brace""]}},
            ""required"":[""level"",""start_x"",""start_y"",""end_x"",""end_y""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            if (!StructuralHelpers.TrySymbol(doc, p, BuiltInCategory.OST_StructuralFraming, "structural framing", out var symbol, out var failure))
                return failure;
            if (!Lookup.TryLevel(doc, p.Value<string>("level"), out var level, out failure)) return failure;

            var startZ = p.Value<double?>("start_z");
            var endZ = p.Value<double?>("end_z");
            var start = new XYZ(Mm.ToFeet(p.Value<double>("start_x")), Mm.ToFeet(p.Value<double>("start_y")),
                startZ.HasValue ? Mm.ToFeet(startZ.Value) : level.ProjectElevation);
            var end = new XYZ(Mm.ToFeet(p.Value<double>("end_x")), Mm.ToFeet(p.Value<double>("end_y")),
                endZ.HasValue ? Mm.ToFeet(endZ.Value) : level.ProjectElevation);
            if (start.DistanceTo(end) <= app.Application.ShortCurveTolerance)
                return CommandResult.Fail("LINE_TOO_SHORT: start and end are closer than Revit's shortest allowed line ("
                    + Mm.FromFeet(app.Application.ShortCurveTolerance) + " mm).");
            var usage = p.Value<string>("usage") ?? "beam";
            var structuralType = usage == "brace" ? StructuralType.Brace : StructuralType.Beam;

            using (var tx = new Transaction(doc, "Plinth: Create Structural " + (usage == "brace" ? "Brace" : "Beam")))
            {
                tx.Start();
                try
                {
                    if (!symbol.IsActive)
                    {
                        symbol.Activate();
                        doc.Regenerate();
                    }
                    var beam = doc.Create.NewFamilyInstance(Line.CreateBound(start, end), symbol, level, structuralType);
                    if (beam == null) throw new InvalidOperationException("Revit returned no framing element.");

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    var curve = (beam.Location as LocationCurve)?.Curve;
                    return CommandResult.Ok(new
                    {
                        element_id = RevitCompat.GetId(beam.Id),
                        type_id = RevitCompat.GetId(symbol.Id),
                        family_name = symbol.FamilyName,
                        type_name = symbol.Name,
                        level = level.Name,
                        usage,
                        structural_type = structuralType.ToString(),
                        start_mm = StructuralHelpers.PointMm(curve?.GetEndPoint(0)),
                        end_mm = StructuralHelpers.PointMm(curve?.GetEndPoint(1))
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the " + usage + ": " + ex.Message + " Nothing was created.");
                }
            }
        }
    }
}
