// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Mep/ConnectMepElementsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: connector_index_1/2 renamed connector_id_1/2 (they always held Connector.Id, not an ordinal);
// every refusal (no compatible pair, system type mismatch, Revit failure) is a failure with a CODE and the details
// as data (rvt-mcp returned success with connected=false); errors are "CODE: message".

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Mep
{
    public sealed class ConnectMepElementsHandler : IRevitCommand
    {
        public string Name => "connect_mep_elements";
        public string Description =>
            "Connect two MEP elements (e.g. a duct to equipment). Without connector ids, the nearest pair of open physical connectors "
            + "of the same domain is used; with connector_id_1 and connector_id_2 (connector_id from get_mep_element_connectors) those "
            + "exact connectors. Different assigned piping/HVAC system types are refused. A direct connection or one shared pipe/duct "
            + "fitting counts as connected; calling again is a no-op. Returns both connector origins in mm and the gap between them.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_id_1"":{""type"":""integer""},
            ""element_id_2"":{""type"":""integer""},
            ""connector_id_1"":{""type"":""integer""},
            ""connector_id_2"":{""type"":""integer""}},
            ""required"":[""element_id_1"",""element_id_2""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var id1 = p.Value<long>("element_id_1");
            var id2 = p.Value<long>("element_id_2");
            if (id1 == id2) return CommandResult.Fail("INVALID_PARAMS: element_id_1 and element_id_2 must be different elements.");
            var connectorId1 = p.Value<int?>("connector_id_1");
            var connectorId2 = p.Value<int?>("connector_id_2");
            if (connectorId1.HasValue != connectorId2.HasValue)
                return CommandResult.Fail("INVALID_PARAMS: give both connector_id_1 and connector_id_2, or neither.");

            var element1 = doc.GetElement(RevitCompat.ToElementId(id1));
            if (element1 == null) return CommandResult.Fail("ELEMENT_NOT_FOUND: no element has id " + id1 + ".");
            var element2 = doc.GetElement(RevitCompat.ToElementId(id2));
            if (element2 == null) return CommandResult.Fail("ELEMENT_NOT_FOUND: no element has id " + id2 + ".");
            var cm1 = MepHelpers.GetConnectorManager(element1);
            if (cm1 == null) return CommandResult.Fail("NO_CONNECTORS: element " + id1 + " has no MEP connectors.");
            var cm2 = MepHelpers.GetConnectorManager(element2);
            if (cm2 == null) return CommandResult.Fail("NO_CONNECTORS: element " + id2 + " has no MEP connectors.");

            Connector c1, c2;
            if (connectorId1.HasValue)
            {
                c1 = MepHelpers.FindConnector(cm1, connectorId1.Value);
                if (c1 == null) return CommandResult.Fail("CONNECTOR_NOT_FOUND: element " + id1 + " has no connector with id " + connectorId1.Value + ".");
                c2 = MepHelpers.FindConnector(cm2, connectorId2.Value);
                if (c2 == null) return CommandResult.Fail("CONNECTOR_NOT_FOUND: element " + id2 + " has no connector with id " + connectorId2.Value + ".");
            }
            else if (!FindConnectedPair(cm1, cm2, out c1, out c2) && !FindNearestOpenPair(cm1, cm2, out c1, out c2))
            {
                return CommandResult.Fail("NO_COMPATIBLE_CONNECTORS: no pair of open physical connectors of the same domain was found.");
            }

            var origin1 = MepHelpers.SafeOrigin(c1);
            var origin2 = MepHelpers.SafeOrigin(c2);
            double? gapMm = origin1 != null && origin2 != null ? Mm.FromFeet(origin1.DistanceTo(origin2)) : (double?)null;

            MEPSystemType type1, type2;
            bool alreadyConnected;
            try
            {
                if (!MepHelpers.IsPhysical(c1) || !MepHelpers.IsPhysical(c2) || c1.Domain != c2.Domain)
                    return CommandResult.Fail("INCOMPATIBLE_CONNECTORS: both connectors must be physical (End or Curve) and of the same domain.");
                type1 = MepConnectionSystemType.Read(c1);
                type2 = MepConnectionSystemType.Read(c2);
                if (MepConnectionSystemType.Mismatch(type1, type2))
                    return CommandResult.Fail("SYSTEM_TYPE_MISMATCH: the two connectors belong to different system types; nothing was connected. "
                        + "Recreate or reassign the wrong element.", new
                        {
                            system_type_id_1 = RevitCompat.GetId(type1.Id), system_type_1 = type1.Name,
                            system_type_id_2 = RevitCompat.GetId(type2.Id), system_type_2 = type2.Name
                        });
                alreadyConnected = HasPhysicalConnection(c1, c2);
                if (!alreadyConnected && (c1.IsConnected || c2.IsConnected))
                    return CommandResult.Fail("CONNECTOR_IN_USE: a chosen connector is already connected to something else.");
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("READ_FAILED: cannot check the chosen connectors: " + ex.Message);
            }

            if (!alreadyConnected)
            {
                using (var tx = new Transaction(doc, "Plinth: Connect MEP Elements"))
                {
                    tx.Start();
                    try
                    {
                        c1.ConnectTo(c2);
                        doc.Regenerate();
                        if (!HasPhysicalConnection(c1, c2))
                            throw new InvalidOperationException("Revit did not establish the connection.");
                        type1 = MepConnectionSystemType.Read(c1);
                        type2 = MepConnectionSystemType.Read(c2);
                        if (tx.Commit() != TransactionStatus.Committed)
                            return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                        return CommandResult.Fail("CONNECT_FAILED: could not connect the connectors: " + ex.Message,
                            new { connector_1_origin_mm = MepHelpers.PointMm(origin1), connector_2_origin_mm = MepHelpers.PointMm(origin2), gap_mm = gapMm });
                    }
                }
            }

            return CommandResult.Ok(new
            {
                connected = true,
                already_connected = alreadyConnected,
                element_id_1 = id1,
                element_id_2 = id2,
                connector_id_1 = c1.Id,
                connector_id_2 = c2.Id,
                connector_1_origin_mm = MepHelpers.PointMm(origin1),
                connector_2_origin_mm = MepHelpers.PointMm(origin2),
                gap_mm = gapMm,
                system_type_id_1 = type1 == null ? (long?)null : RevitCompat.GetId(type1.Id),
                system_type_1 = type1?.Name,
                system_type_id_2 = type2 == null ? (long?)null : RevitCompat.GetId(type2.Id),
                system_type_2 = type2?.Name
            });
        }

        private static bool FindNearestOpenPair(ConnectorManager cm1, ConnectorManager cm2, out Connector best1, out Connector best2)
        {
            best1 = null;
            best2 = null;
            var bestDistance = double.MaxValue;
            foreach (Connector a in cm1.Connectors)
            {
                if (!IsOpenPhysical(a)) continue;
                var oa = MepHelpers.SafeOrigin(a);
                if (oa == null) continue;
                foreach (Connector b in cm2.Connectors)
                {
                    if (!IsOpenPhysical(b) || a.Domain != b.Domain) continue;
                    var ob = MepHelpers.SafeOrigin(b);
                    if (ob == null) continue;
                    var distance = oa.DistanceTo(ob);
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best1 = a;
                    best2 = b;
                }
            }
            return best1 != null;
        }

        private static bool IsOpenPhysical(Connector c)
        {
            try { return MepHelpers.IsPhysical(c) && !c.IsConnected; }
            catch { return false; }
        }

        /// <summary>
        /// Directly connected, or ConnectTo inserted one pipe or duct fitting between them: two distinct physical
        /// ports of the same fitting. Arbitrary paths through equipment do not count.
        /// </summary>
        private static bool HasPhysicalConnection(Connector first, Connector second)
        {
            if (first.IsConnectedTo(second)) return true;
            foreach (Connector firstPort in first.AllRefs)
            {
                if (!MepHelpers.IsPhysical(firstPort) || firstPort.Domain != first.Domain
                    || !(firstPort.Owner is FamilyInstance) || !first.IsConnectedTo(firstPort)) continue;
                var bic = firstPort.Owner.Category?.BuiltInCategory;
                if (bic != BuiltInCategory.OST_PipeFitting && bic != BuiltInCategory.OST_DuctFitting) continue;
                foreach (Connector secondPort in second.AllRefs)
                {
                    if (MepHelpers.IsPhysical(secondPort) && secondPort.Domain == second.Domain
                        && firstPort.Owner.Id == secondPort.Owner.Id && firstPort.Id != secondPort.Id
                        && second.IsConnectedTo(secondPort)) return true;
                }
            }
            return false;
        }

        private static bool FindConnectedPair(ConnectorManager first, ConnectorManager second, out Connector a, out Connector b)
        {
            foreach (Connector x in first.Connectors)
                foreach (Connector y in second.Connectors)
                    if (MepHelpers.IsPhysical(x) && MepHelpers.IsPhysical(y) && x.Domain == y.Domain && HasPhysicalConnection(x, y))
                    {
                        a = x;
                        b = y;
                        return true;
                    }
            a = null;
            b = null;
            return false;
        }
    }
}
