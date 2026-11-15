// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Elements/CreatePointBasedElementHandler.cs, commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for B2M (PRD improvement for doors and windows):
// - hostOffset places a hosted family by distance along its wall from the wall's start, so callers
//   need no geometry; sillHeight sets the sill; flipFacing/flipHand set orientation;
// - typeName (with familyName and builtInCategory) as well as typeId;
// - an unknown level is an error with the valid options (no silent first-level fallback);
// - kept: placement-type checks, host and 1 mm position verification before commit.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Create
{
    public sealed class CreatePointBasedElementHandler : IRevitCommand
    {
        private static readonly double Tolerance = Mm.ToFeet(1.0);

        public string Name => "create_point_based_element";
        public string Description =>
            "Place a family instance (door, window, furniture) on a named level. Hosted doors and windows need host_id of a wall and "
            + "either hostOffset (mm along the wall from its start) or x/y. Optional sillHeight (mm), flipFacing, flipHand. "
            + "Choose the type by typeId or typeName (+ familyName, builtInCategory such as OST_Doors).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""typeId"":{""type"":""integer""},
            ""typeName"":{""type"":""string""},
            ""familyName"":{""type"":""string""},
            ""builtInCategory"":{""type"":""string""},
            ""level"":{""type"":""string""},
            ""x"":{""type"":""number""},""y"":{""type"":""number""},""z"":{""type"":""number""},
            ""host_id"":{""type"":""integer""},
            ""hostOffset"":{""type"":""number""},
            ""sillHeight"":{""type"":""number""},
            ""flipFacing"":{""type"":""boolean""},
            ""flipHand"":{""type"":""boolean""}},
            ""required"":[""level""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            BuiltInCategory? category = null;
            var categoryName = p.Value<string>("builtInCategory");
            if (!string.IsNullOrEmpty(categoryName))
            {
                if (!Lookup.TryBuiltInCategory(categoryName, out var bic, out var bad)) return bad;
                category = bic;
            }
            if (!Lookup.TryFamilySymbol(doc, p.Value<long?>("typeId"), p.Value<string>("familyName"), p.Value<string>("typeName"),
                    category, out var symbol, out var failure)) return failure;
            if (!Lookup.TryLevel(doc, p.Value<string>("level"), out var level, out failure)) return failure;

            var hostId = p.Value<long?>("host_id");
            var hostOffset = p.Value<double?>("hostOffset");
            var hasXY = p["x"] != null && p["y"] != null;
            if (hostOffset.HasValue == hasXY)
                return CommandResult.Fail("INVALID_PARAMS: give either hostOffset (with host_id) or x and y, not both.");
            if (hostOffset.HasValue && !hostId.HasValue)
                return CommandResult.Fail("INVALID_PARAMS: hostOffset needs host_id.");

            var placement = symbol.Family.FamilyPlacementType;
            Element host = null;
            string warning = null;
            switch (placement)
            {
                case FamilyPlacementType.OneLevelBased:
                    if (hostId.HasValue)
                    {
                        if (hostOffset.HasValue) return CommandResult.Fail("INVALID_PARAMS: this family is not hosted; use x and y.");
                        warning = "host_id ignored: this family does not use a host.";
                    }
                    break;
                case FamilyPlacementType.OneLevelBasedHosted:
                    if (!hostId.HasValue)
                        return CommandResult.Fail("HOST_REQUIRED: this family is hosted; give host_id of the wall. Nothing was created.");
                    host = doc.GetElement(RevitCompat.ToElementId(hostId.Value));
                    if (host == null) return CommandResult.Fail("ELEMENT_NOT_FOUND: host " + hostId.Value + " is not in this document.");
                    var bic = symbol.Category?.BuiltInCategory;
                    if ((bic == BuiltInCategory.OST_Doors || bic == BuiltInCategory.OST_Windows) && !(host is Wall))
                        return CommandResult.Fail("HOST_NOT_WALL: doors and windows need a wall as host.");
                    break;
                case FamilyPlacementType.WorkPlaneBased:
                    return CommandResult.Fail("UNSUPPORTED_PLACEMENT: work-plane-based families need a face and orientation; not supported. Nothing was created.");
                default:
                    return CommandResult.Fail("UNSUPPORTED_PLACEMENT: placement type " + placement + " is not supported. Nothing was created.");
            }

            XYZ point;
            if (hostOffset.HasValue)
            {
                var curve = ((host as Wall)?.Location as LocationCurve)?.Curve as Line;
                if (curve == null) return CommandResult.Fail("HOST_NOT_STRAIGHT_WALL: hostOffset needs a straight wall as host.");
                var length = curve.Length;
                var offset = Mm.ToFeet(hostOffset.Value);
                if (offset < 0 || offset > length)
                    return CommandResult.Fail("OFFSET_OUT_OF_RANGE: hostOffset must be between 0 and the wall length (" + Mm.FromFeet(length) + " mm).");
                var along = curve.GetEndPoint(0) + curve.Direction * offset;
                point = new XYZ(along.X, along.Y, level.ProjectElevation);
            }
            else
            {
                var z = p.Value<double?>("z");
                point = new XYZ(Mm.ToFeet(p.Value<double>("x")), Mm.ToFeet(p.Value<double>("y")),
                    z.HasValue ? Mm.ToFeet(z.Value) : level.ProjectElevation);
            }

            using (var tx = new Transaction(doc, "B2M: Place " + (symbol.Category?.Name ?? "instance")))
            {
                tx.Start();
                try
                {
                    if (!symbol.IsActive)
                    {
                        symbol.Activate();
                        doc.Regenerate();
                    }

                    var instance = host == null
                        ? doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural)
                        : doc.Create.NewFamilyInstance(point, symbol, host, level, StructuralType.NonStructural);
                    if (instance == null) throw new InvalidOperationException("Revit returned no instance.");

                    var sill = p.Value<double?>("sillHeight");
                    if (sill.HasValue)
                    {
                        var sillParam = instance.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                        if (sillParam == null || sillParam.IsReadOnly)
                            throw new InvalidOperationException("this family has no settable sill height.");
                        sillParam.Set(Mm.ToFeet(sill.Value));
                    }
                    if ((p.Value<bool?>("flipFacing") ?? false) && instance.CanFlipFacing) instance.flipFacing();
                    if ((p.Value<bool?>("flipHand") ?? false) && instance.CanFlipHand) instance.flipHand();
                    doc.Regenerate();

                    var actualHost = instance.Host;
                    if (host != null && (actualHost == null || actualHost.Id != host.Id))
                        throw new InvalidOperationException("Revit did not assign the requested host.");
                    var actual = (instance.Location as LocationPoint)?.Point;
                    // Sill height moves hosted openings vertically, so compare plan position then.
                    var miss = actual == null ? double.MaxValue
                        : sill.HasValue || host != null ? new XYZ(actual.X - point.X, actual.Y - point.Y, 0).GetLength()
                        : actual.DistanceTo(point);
                    if (miss > Tolerance)
                        throw new InvalidOperationException("Revit did not place the instance within 1 mm of the requested point "
                            + "(requested " + Describe(point) + ", got " + (actual == null ? "none" : Describe(actual)) + ").");

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    var sillNow = instance.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                    return CommandResult.Ok(new
                    {
                        elementId = RevitCompat.GetId(instance.Id),
                        familyName = symbol.FamilyName,
                        typeName = symbol.Name,
                        typeId = RevitCompat.GetId(symbol.Id),
                        builtInCategory = Lookup.BuiltInName(instance.Category),
                        placement_type = placement.ToString(),
                        host_id = actualHost == null ? (long?)null : RevitCompat.GetId(actualHost.Id),
                        level = level.Name,
                        location_mm = new { x = Mm.FromFeet(actual.X), y = Mm.FromFeet(actual.Y), z = Mm.FromFeet(actual.Z) },
                        sillHeightMm = sillNow == null ? (double?)null : Mm.FromFeet(sillNow.AsDouble()),
                        warning
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not place the instance: " + ex.Message + " Nothing was created.");
                }
            }
        }

        private static string Describe(XYZ p) =>
            FormattableString.Invariant($"{Mm.FromFeet(p.X)}, {Mm.FromFeet(p.Y)}, {Mm.FromFeet(p.Z)} mm");
    }
}
