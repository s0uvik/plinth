// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Structural/CreateRebarSetHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: the bar type (bar_type_id or bar_type_name) is required and a miss lists the options (rvt-mcp used
// the first bar type); the host is checked with RebarHostData.IsValidHost; MaximumSpacing needs an explicit array_length
// (rvt-mcp guessed it from the host's largest bounding-box side); FixedNumber needs quantity >= 2 and spacing; an
// optional normal (normal_x/y/z) sets the direction the set is laid out in (rvt-mcp's derived normal stays the
// default); the R26+/R24-25 CreateFromCurves overloads are kept behind REVIT2026/REVIT2027 symbols.

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Structural
{
    public sealed class CreateRebarSetHandler : IRevitCommand
    {
        public string Name => "create_rebar_set";
        public string Description =>
            "Create a straight rebar set in a concrete host (host_id: column, beam, wall, floor or foundation) along start to end "
            + "(mm, internal coordinates). Bar type by bar_type_id or bar_type_name. layout_rule: Single (default), FixedNumber "
            + "(quantity >= 2 and spacing in mm) or MaximumSpacing (spacing and array_length in mm). Bars are repeated along the normal "
            + "(normal_x/y/z; default horizontal and perpendicular to the bar, or X for vertical bars).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""host_id"":{""type"":""integer""},
            ""bar_type_id"":{""type"":""integer""},
            ""bar_type_name"":{""type"":""string""},
            ""layout_rule"":{""type"":""string"",""enum"":[""Single"",""FixedNumber"",""MaximumSpacing""]},
            ""spacing"":{""type"":""number""},
            ""quantity"":{""type"":""integer""},
            ""array_length"":{""type"":""number""},
            ""start_x"":{""type"":""number""},""start_y"":{""type"":""number""},""start_z"":{""type"":""number""},
            ""end_x"":{""type"":""number""},""end_y"":{""type"":""number""},""end_z"":{""type"":""number""},
            ""normal_x"":{""type"":""number""},""normal_y"":{""type"":""number""},""normal_z"":{""type"":""number""}},
            ""required"":[""host_id"",""start_x"",""start_y"",""start_z"",""end_x"",""end_y"",""end_z""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var rule = p.Value<string>("layout_rule") ?? "Single";
            var spacing = p.Value<double?>("spacing");
            var quantity = p.Value<int?>("quantity");
            var arrayLength = p.Value<double?>("array_length");
            switch (rule)
            {
                case "FixedNumber":
                    if (!quantity.HasValue || quantity.Value < 2 || !spacing.HasValue || spacing.Value <= 0)
                        return CommandResult.Fail("INVALID_PARAMS: FixedNumber needs quantity of 2 or more and spacing above 0 mm.");
                    if (arrayLength.HasValue) return CommandResult.Fail("INVALID_PARAMS: FixedNumber takes quantity and spacing, not array_length.");
                    break;
                case "MaximumSpacing":
                    if (!spacing.HasValue || spacing.Value <= 0 || !arrayLength.HasValue || arrayLength.Value <= 0)
                        return CommandResult.Fail("INVALID_PARAMS: MaximumSpacing needs spacing and array_length above 0 mm.");
                    if (quantity.HasValue) return CommandResult.Fail("INVALID_PARAMS: MaximumSpacing takes spacing and array_length, not quantity.");
                    break;
                default:
                    if (spacing.HasValue || quantity.HasValue || arrayLength.HasValue)
                        return CommandResult.Fail("INVALID_PARAMS: a Single bar takes no spacing, quantity or array_length.");
                    break;
            }

            var hostId = p.Value<long>("host_id");
            var host = doc.GetElement(RevitCompat.ToElementId(hostId));
            if (host == null) return CommandResult.Fail("ELEMENT_NOT_FOUND: no element has id " + hostId + ".");
            if (!RebarHostData.IsValidHost(host))
                return CommandResult.Fail("INVALID_HOST: element " + hostId + " (" + host.Category?.Name + ") cannot host rebar; use a concrete structural element.");
            if (!StructuralHelpers.TryType<RebarBarType>(doc, p.Value<long?>("bar_type_id"), p.Value<string>("bar_type_name"),
                    "rebar bar type", "bar_type_id", "bar_type_name", out var barType, out var failure)) return failure;

            var start = new XYZ(Mm.ToFeet(p.Value<double>("start_x")), Mm.ToFeet(p.Value<double>("start_y")), Mm.ToFeet(p.Value<double>("start_z")));
            var end = new XYZ(Mm.ToFeet(p.Value<double>("end_x")), Mm.ToFeet(p.Value<double>("end_y")), Mm.ToFeet(p.Value<double>("end_z")));
            if (start.DistanceTo(end) <= app.Application.ShortCurveTolerance)
                return CommandResult.Fail("LINE_TOO_SHORT: start and end are closer than Revit's shortest allowed line.");
            var direction = (end - start).Normalize();

            XYZ normal;
            if (p["normal_x"] != null || p["normal_y"] != null || p["normal_z"] != null)
            {
                normal = new XYZ(p.Value<double?>("normal_x") ?? 0, p.Value<double?>("normal_y") ?? 0, p.Value<double?>("normal_z") ?? 0);
                if (normal.GetLength() < 1e-9) return CommandResult.Fail("INVALID_PARAMS: the normal must not be a zero vector.");
                normal = normal.Normalize();
                if (Math.Abs(normal.DotProduct(direction)) > 1e-6)
                    return CommandResult.Fail("INVALID_PARAMS: the normal must be perpendicular to the bar (start to end).");
            }
            else
            {
                normal = Math.Abs(direction.Z) > 0.999 ? XYZ.BasisX : direction.CrossProduct(XYZ.BasisZ).Normalize();
            }

            using (var tx = new Transaction(doc, "Plinth: Create Rebar Set"))
            {
                tx.Start();
                try
                {
                    var rebar = CreateFromCurves(doc, barType, host, normal, new List<Curve> { Line.CreateBound(start, end) });
                    if (rebar == null) throw new InvalidOperationException("Revit returned no rebar.");
                    var accessor = rebar.GetShapeDrivenAccessor();
                    if (accessor == null) throw new InvalidOperationException("the new rebar is not shape-driven, so no layout can be set.");
                    switch (rule)
                    {
                        case "FixedNumber":
                            accessor.SetLayoutAsFixedNumber(quantity.Value, Mm.ToFeet(spacing.Value) * (quantity.Value - 1), true, true, true);
                            break;
                        case "MaximumSpacing":
                            accessor.SetLayoutAsMaximumSpacing(Mm.ToFeet(spacing.Value), Mm.ToFeet(arrayLength.Value), true, true, true);
                            break;
                        default:
                            accessor.SetLayoutAsSingle();
                            break;
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    return CommandResult.Ok(new
                    {
                        rebar_id = RevitCompat.GetId(rebar.Id),
                        host_id = hostId,
                        bar_type_id = RevitCompat.GetId(barType.Id),
                        bar_type_name = barType.Name,
                        layout_rule = rebar.LayoutRule.ToString(),
                        quantity = rebar.Quantity,
                        spacing_mm = rule == "Single" ? (double?)null : Mm.FromFeet(rebar.MaxSpacing),
                        array_length_mm = rule == "Single" ? (double?)null : Mm.FromFeet(accessor.ArrayLength)
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the rebar set: " + ex.Message + " Nothing was created.");
                }
            }
        }

        private static Rebar CreateFromCurves(Document doc, RebarBarType barType, Element host, XYZ normal, IList<Curve> curves)
        {
#if REVIT2026 || REVIT2027
            // Revit 2026 replaced hook types with BarTerminationsData (default: no hooks or end treatments).
            using (var terminations = new BarTerminationsData(doc))
            {
#if REVIT2026
                // Keep rvt-mcp's R26 orientations; R27 keeps its own defaults.
                terminations.TerminationOrientationAtStart = RebarTerminationOrientation.Right;
                terminations.TerminationOrientationAtEnd = RebarTerminationOrientation.Left;
#endif
                return Rebar.CreateFromCurves(doc, RebarStyle.Standard, barType, host, normal, curves, terminations, true, true);
            }
#else
            return Rebar.CreateFromCurves(doc, RebarStyle.Standard, barType, null, null, host, normal, curves,
                RebarHookOrientation.Right, RebarHookOrientation.Left, true, true);
#endif
        }
    }
}
