// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Structural/CreateFoundationIsolatedHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: level (by name) and foundation type (type_id, or type_name + family_name) are required and a miss lists
// the options (rvt-mcp used the first foundation type and the lowest level); the position is x/y or host_column_id, not
// both, and a host_column_id that is not a point-placed structural column is an error (rvt-mcp silently fell back to
// x/y); z defaults to the level elevation (rvt-mcp used 0).

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Structural
{
    public sealed class CreateFoundationIsolatedHandler : IRevitCommand
    {
        public string Name => "create_foundation_isolated";
        public string Description =>
            "Place an isolated (pad) footing on a named level, at x, y in mm or under a structural column (host_column_id). "
            + "Type by type_id or type_name (+ family_name). Optional z (absolute elevation in mm, default the level) and rotation_deg.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""type_id"":{""type"":""integer""},
            ""type_name"":{""type"":""string""},
            ""family_name"":{""type"":""string""},
            ""level"":{""type"":""string""},
            ""x"":{""type"":""number""},""y"":{""type"":""number""},""z"":{""type"":""number""},
            ""host_column_id"":{""type"":""integer""},
            ""rotation_deg"":{""type"":""number""}},
            ""required"":[""level""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var hostColumnId = p.Value<long?>("host_column_id");
            var hasXY = p["x"] != null && p["y"] != null;
            if (hostColumnId.HasValue == hasXY || (!hasXY && (p["x"] != null || p["y"] != null)))
                return CommandResult.Fail("INVALID_PARAMS: give either x and y, or host_column_id.");

            if (!StructuralHelpers.TrySymbol(doc, p, BuiltInCategory.OST_StructuralFoundation, "structural foundation", out var symbol, out var failure))
                return failure;
            if (!Lookup.TryLevel(doc, p.Value<string>("level"), out var level, out failure)) return failure;

            var zMm = p.Value<double?>("z");
            var z = zMm.HasValue ? Mm.ToFeet(zMm.Value) : level.ProjectElevation;
            XYZ point;
            if (hostColumnId.HasValue)
            {
                var column = doc.GetElement(RevitCompat.ToElementId(hostColumnId.Value)) as FamilyInstance;
                var at = (column?.Location as LocationPoint)?.Point;
                if (column == null || column.Category?.BuiltInCategory != BuiltInCategory.OST_StructuralColumns || at == null)
                    return CommandResult.Fail("NOT_A_COLUMN: element " + hostColumnId.Value + " is not a vertical structural column.");
                point = new XYZ(at.X, at.Y, z);
            }
            else
            {
                point = new XYZ(Mm.ToFeet(p.Value<double>("x")), Mm.ToFeet(p.Value<double>("y")), z);
            }

            using (var tx = new Transaction(doc, "B2M: Create Isolated Foundation"))
            {
                tx.Start();
                try
                {
                    if (!symbol.IsActive)
                    {
                        symbol.Activate();
                        doc.Regenerate();
                    }
                    var footing = doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.Footing);
                    if (footing == null) throw new InvalidOperationException("Revit returned no footing.");
                    StructuralHelpers.RotateAboutZ(doc, footing.Id, point, p.Value<double?>("rotation_deg") ?? 0);

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    return CommandResult.Ok(new
                    {
                        footing_id = RevitCompat.GetId(footing.Id),
                        type_id = RevitCompat.GetId(symbol.Id),
                        family_name = symbol.FamilyName,
                        type_name = symbol.Name,
                        level = level.Name,
                        host_column_id = hostColumnId,
                        location_mm = StructuralHelpers.PointMm((footing.Location as LocationPoint)?.Point)
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the footing: " + ex.Message + " Nothing was created.");
                }
            }
        }
    }
}
