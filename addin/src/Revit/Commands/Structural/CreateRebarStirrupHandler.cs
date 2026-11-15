// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Structural/CreateRebarStirrupHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: bar type (bar_type_id or bar_type_name) and rebar shape (shape_id or shape_name) are required and a
// miss lists the options (rvt-mcp used the first bar type and the first shape); the host is checked with
// RebarHostData.IsValidHost; a zero direction vector is an error (rvt-mcp silently used world X or Y); origin field
// names shortened to origin_x/y/z.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Structural
{
    public sealed class CreateRebarStirrupHandler : IRevitCommand
    {
        public string Name => "create_rebar_stirrup";
        public string Description =>
            "Create one shape-driven bar (typically a closed stirrup) in a concrete host (host_id) from a rebar shape (shape_id or "
            + "shape_name, e.g. 'M_T1') and bar type (bar_type_id or bar_type_name). origin_x/y/z in mm; the shape's x and y "
            + "directions are x_vec_* (default world X) and y_vec_* (default world Y), unitless.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""host_id"":{""type"":""integer""},
            ""bar_type_id"":{""type"":""integer""},
            ""bar_type_name"":{""type"":""string""},
            ""shape_id"":{""type"":""integer""},
            ""shape_name"":{""type"":""string""},
            ""origin_x"":{""type"":""number""},""origin_y"":{""type"":""number""},""origin_z"":{""type"":""number""},
            ""x_vec_x"":{""type"":""number""},""x_vec_y"":{""type"":""number""},""x_vec_z"":{""type"":""number""},
            ""y_vec_x"":{""type"":""number""},""y_vec_y"":{""type"":""number""},""y_vec_z"":{""type"":""number""}},
            ""required"":[""host_id"",""origin_x"",""origin_y"",""origin_z""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var xVec = new XYZ(p.Value<double?>("x_vec_x") ?? 1, p.Value<double?>("x_vec_y") ?? 0, p.Value<double?>("x_vec_z") ?? 0);
            var yVec = new XYZ(p.Value<double?>("y_vec_x") ?? 0, p.Value<double?>("y_vec_y") ?? 1, p.Value<double?>("y_vec_z") ?? 0);
            if (xVec.GetLength() < 1e-9 || yVec.GetLength() < 1e-9)
                return CommandResult.Fail("INVALID_PARAMS: x_vec and y_vec must not be zero vectors.");
            xVec = xVec.Normalize();
            yVec = yVec.Normalize();
            if (xVec.CrossProduct(yVec).GetLength() < 1e-6)
                return CommandResult.Fail("INVALID_PARAMS: x_vec and y_vec must not be parallel.");

            var hostId = p.Value<long>("host_id");
            var host = doc.GetElement(RevitCompat.ToElementId(hostId));
            if (host == null) return CommandResult.Fail("ELEMENT_NOT_FOUND: no element has id " + hostId + ".");
            if (!RebarHostData.IsValidHost(host))
                return CommandResult.Fail("INVALID_HOST: element " + hostId + " (" + host.Category?.Name + ") cannot host rebar; use a concrete structural element.");
            if (!StructuralHelpers.TryType<RebarBarType>(doc, p.Value<long?>("bar_type_id"), p.Value<string>("bar_type_name"),
                    "rebar bar type", "bar_type_id", "bar_type_name", out var barType, out var failure)) return failure;
            if (!StructuralHelpers.TryType<RebarShape>(doc, p.Value<long?>("shape_id"), p.Value<string>("shape_name"),
                    "rebar shape", "shape_id", "shape_name", out var shape, out failure)) return failure;

            var origin = new XYZ(Mm.ToFeet(p.Value<double>("origin_x")), Mm.ToFeet(p.Value<double>("origin_y")), Mm.ToFeet(p.Value<double>("origin_z")));
            using (var tx = new Transaction(doc, "B2M: Create Rebar Stirrup"))
            {
                tx.Start();
                try
                {
                    var rebar = Rebar.CreateFromRebarShape(doc, shape, barType, host, origin, xVec, yVec);
                    if (rebar == null) throw new InvalidOperationException("Revit returned no rebar.");

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    return CommandResult.Ok(new
                    {
                        rebar_id = RevitCompat.GetId(rebar.Id),
                        host_id = hostId,
                        bar_type_id = RevitCompat.GetId(barType.Id),
                        bar_type_name = barType.Name,
                        shape_id = RevitCompat.GetId(shape.Id),
                        shape_name = shape.Name
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the stirrup: " + ex.Message + " Nothing was created.");
                }
            }
        }
    }
}
