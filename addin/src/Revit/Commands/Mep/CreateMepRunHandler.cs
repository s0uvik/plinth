// New in Plinth (Step 10, F5): create_mep_run draws a pipe, duct, conduit or cable tray run along a polyline of 3D points
// in one transaction: one segment per leg (points on a straight line are merged, RunPath), an elbow at every bend, and
// optionally each end joined to an element: a fixture's or equipment's nearest free connector of the run's domain whose
// system classification fits (ConnectorMatch), a pipe's or duct's free end, or the middle of a pipe or duct (the main is
// broken there and a tee added). The run is extended from its last point to the connector or main. A fitting or
// connection Revit refuses is reported in failed_connections with the reason and nothing of it is left (each in its own
// sub-transaction); require_connections makes any such failure undo the whole run. created_ids lists every element
// added (segments, fittings, the far piece of a broken main); modified_ids the mains that were broken.
// Tees onto a multi-segment main (2026-11-27, live plumbing builds): the caller knows one id per main (its first segment),
// but the join point usually lies on a later segment (a branch teeing into segment 4 of a 5-segment downtake), and each
// tee breaks the main, so after earlier tees the original id covers only part of it. When the point is not beside the
// given curve, the main's connected network is walked (through its connectors, fittings and in-line accessories, curves
// of the same kind and MEP system, at most 1,000 elements) and the straight segment the point lies beside is teed into
// (MainSegments in Core: 50 mm of main each side, within 300 mm of its line, the nearest if several). joined lists each
// end that was joined to another element than the one asked; OFF_THE_MAIN says how many curves were searched.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Plinth.Revit.Core.Mep;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Mep
{
    public sealed class CreateMepRunHandler : IRevitCommand
    {
        private const int MaxPoints = 200;
        private static readonly double Tolerance = Mm.ToFeet(1.0);

        /// <summary>A tee needs this much main each side of the join point (mm).</summary>
        private const double TeeMarginMm = 50;

        /// <summary>How far beside a connected segment's line the join point may be when the main's network is searched (mm).</summary>
        private const double MaxTeeOffsetMm = 300;

        /// <summary>At most this many elements (curves and fittings) are visited when the main's network is searched.</summary>
        private const int MaxNetworkElements = 1000;

        public string Name => "create_mep_run";
        public string Description =>
            "Create a pipe, duct, conduit or cable tray run along points [[x, y, z], ...] (mm, internal coordinates, z absolute) with an "
            + "elbow at every bend, in one transaction. kind: pipe, duct, conduit or cable_tray; level; type by type_id or type_name; "
            + "pipe and duct need a system type (system_type_id or system_type_name); size: diameter, or width and height. "
            + "start_connect / end_connect {element_id, connector_id?} join that end to an element: a fixture's or equipment's nearest "
            + "free connector of the run's domain and system (the run is extended to it), a free end of a pipe or duct, or the middle "
            + "of a pipe or duct (broken there, with a tee). For a tee the element may be any segment of the main: when the run's end "
            + "is not beside it, the straight pipes or ducts connected to it (through fittings and accessories, same system) are "
            + "searched for the one it is beside (50 mm of main each side, within 300 mm of its line, the nearest). Fittings or "
            + "joins Revit refuses are listed in failed_connections with the reason; require_connections true undoes the whole run "
            + "instead. Returns segment_ids, fitting_ids, created_ids (all added), modified_ids (mains broken for a tee) and joined "
            + "[{at: start|end, requested_id, element_id}] for each end teed into another segment than the one given.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""kind"":{""type"":""string"",""enum"":[""pipe"",""duct"",""conduit"",""cable_tray""]},
            ""level"":{""type"":""string""},
            ""points"":{""type"":""array"",""items"":{""type"":""array""}},
            ""type_id"":{""type"":""integer""},
            ""type_name"":{""type"":""string""},
            ""system_type_id"":{""type"":""integer""},
            ""system_type_name"":{""type"":""string""},
            ""diameter"":{""type"":""number""},
            ""width"":{""type"":""number""},
            ""height"":{""type"":""number""},
            ""start_connect"":{""type"":""object""},
            ""end_connect"":{""type"":""object""},
            ""require_connections"":{""type"":""boolean""}},
            ""required"":[""kind"",""level"",""points""]}";
        public bool ReadOnly => false;

        private sealed class Kind
        {
            public string Name;
            public Domain Domain;
            public Type CurveClass;
            public bool HasSystemType;
            public bool CanBreak;
        }

        private static readonly Dictionary<string, Kind> Kinds = new Dictionary<string, Kind>
        {
            ["pipe"] = new Kind { Name = "pipe", Domain = Domain.DomainPiping, CurveClass = typeof(Pipe), HasSystemType = true, CanBreak = true },
            ["duct"] = new Kind { Name = "duct", Domain = Domain.DomainHvac, CurveClass = typeof(Duct), HasSystemType = true, CanBreak = true },
            ["conduit"] = new Kind { Name = "conduit", Domain = Domain.DomainCableTrayConduit, CurveClass = typeof(Conduit) },
            ["cable_tray"] = new Kind { Name = "cable tray", Domain = Domain.DomainCableTrayConduit, CurveClass = typeof(CableTray) }
        };

        /// <summary>What one end of the run joins.</summary>
        private sealed class EndTarget
        {
            public string End;
            public long ElementId;
            public Element Element;

            /// <summary>The connector to join (a fixture's, or a curve's free end); null for a tee.</summary>
            public Connector Connector;

            /// <summary>For a tee: the main curve and the point on it.</summary>
            public MEPCurve Main;
            public XYZ Point;

            /// <summary>Set when the end cannot be joined; the run is still drawn unless connections are required.</summary>
            public string Error;
        }

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var kindName = p.Value<string>("kind") ?? "";
            if (!Kinds.TryGetValue(kindName, out var kind))
                return CommandResult.Fail("INVALID_PARAMS: kind is pipe, duct, conduit or cable_tray.");
            var diameter = p.Value<double?>("diameter");
            var width = p.Value<double?>("width");
            var height = p.Value<double?>("height");
            var requireConnections = p.Value<bool?>("require_connections") ?? false;
            if (diameter.HasValue && (width.HasValue || height.HasValue))
                return CommandResult.Fail("INVALID_PARAMS: give diameter (round) or width and height (rectangular), not both.");
            if ((kind.Name == "pipe" || kind.Name == "conduit") && (width.HasValue || height.HasValue))
                return CommandResult.Fail("INVALID_PARAMS: a " + kind.Name + " takes diameter, not width and height.");
            if (kind.Name == "cable tray" && diameter.HasValue)
                return CommandResult.Fail("INVALID_PARAMS: a cable tray takes width and height, not diameter.");
            if (!kind.HasSystemType && (p["system_type_id"] != null || p["system_type_name"] != null))
                return CommandResult.Fail("INVALID_PARAMS: a " + kind.Name + " has no system type; omit system_type_id and system_type_name.");

            if (!TryPoints(p["points"], out var points, out var failure)) return failure;
            if (!Lookup.TryLevel(doc, p.Value<string>("level"), out var level, out failure)) return failure;
            if (!TryCurveType(doc, kind, p, out var curveType, out failure)) return failure;
            MEPSystemType systemType = null;
            if (kind.Name == "pipe")
            {
                if (!MepHelpers.TryType(doc, p.Value<long?>("system_type_id"), p.Value<string>("system_type_name"), "piping system",
                        "system_type_id", "system_type_name", out PipingSystemType piping, out failure)) return failure;
                systemType = piping;
            }
            else if (kind.Name == "duct")
            {
                if (!MepHelpers.TryType(doc, p.Value<long?>("system_type_id"), p.Value<string>("system_type_name"), "mechanical system",
                        "system_type_id", "system_type_name", out MechanicalSystemType mechanical, out failure)) return failure;
                systemType = mechanical;
            }
            var runClass = systemType == null ? null : SystemClass(systemType);

            var targets = new List<EndTarget>();
            var shift = 0; // Points added before the caller's first one, so reported indices stay the caller's.
            foreach (var end in new[] { "start", "end" })
            {
                var spec = p[end + "_connect"];
                if (spec == null || spec.Type == JTokenType.Null) continue;
                if (!(spec is JObject o) || o.Value<long?>("element_id") == null)
                    return CommandResult.Fail("INVALID_PARAMS: " + end + "_connect is {element_id, connector_id?}.");
                var near = end == "start" ? points[0] : points[points.Count - 1];
                var target = ResolveTarget(doc, kind, runClass, end, o.Value<long>("element_id"), o.Value<int?>("connector_id"), Feet(near));
                if (target.Error != null && target.Element == null)
                    return CommandResult.Fail(target.Error);
                if (target.Error != null && requireConnections)
                    return CommandResult.Fail(target.Error + " Nothing was created.");
                targets.Add(target);
                if (target.Error != null) continue;
                var to = target.Connector?.Origin ?? target.Point;
                var toMm = new[] { Mm.FromFeet(to.X, 3), Mm.FromFeet(to.Y, 3), Mm.FromFeet(to.Z, 3) };
                if (RunPath.Reach(points, end == "start", toMm, 1.0) == "extended" && end == "start") shift = 1;
            }

            var minSegmentMm = Mm.FromFeet(app.Application.ShortCurveTolerance, 3);
            var plan = RunPath.Build(points, minSegmentMm);
            if (plan.Error != null) return CommandResult.Fail(plan.Error);

            var segments = new List<MEPCurve>();
            var fittingIds = new List<long>();
            var createdIds = new List<long>();
            var modifiedIds = new List<long>();
            var bends = new JArray();
            var connections = new JArray();
            var failed = new JArray();
            var joined = new JArray();

            using (var tx = new Transaction(doc, "Plinth: Create " + kind.Name + " run"))
            {
                tx.Start();
                try
                {
                    for (var i = 0; i < plan.SegmentCount; i++)
                    {
                        var segment = CreateSegment(doc, kind, curveType, systemType, level, Feet(plan.Points[i]), Feet(plan.Points[i + 1]));
                        if (diameter.HasValue)
                            MepHelpers.SetLengthMm(segment, kind.Name == "pipe" ? BuiltInParameter.RBS_PIPE_DIAMETER_PARAM
                                : kind.Name == "conduit" ? BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM
                                : BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, diameter.Value, "diameter");
                        if (width.HasValue)
                            MepHelpers.SetLengthMm(segment, kind.Name == "cable tray" ? BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM
                                : BuiltInParameter.RBS_CURVE_WIDTH_PARAM, width.Value, "width");
                        if (height.HasValue)
                            MepHelpers.SetLengthMm(segment, kind.Name == "cable tray" ? BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM
                                : BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, height.Value, "height");
                        segments.Add(segment);
                        createdIds.Add(RevitCompat.GetId(segment.Id));
                    }
                    doc.Regenerate();

                    foreach (var bend in plan.Bends)
                    {
                        var at = Feet(plan.Points[bend.Index]);
                        var fitting = TryStep(doc, failed, requireConnections, new JObject { ["at"] = "bend", ["point_index"] = bend.SourceIndex - shift },
                            () => doc.Create.NewElbowFitting(ConnectorAt(segments[bend.Index - 1], at), ConnectorAt(segments[bend.Index], at)), null).Item1;
                        if (fitting != null)
                        {
                            fittingIds.Add(RevitCompat.GetId(fitting.Id));
                            createdIds.Add(RevitCompat.GetId(fitting.Id));
                        }
                        bends.Add(new JObject
                        {
                            ["point_index"] = bend.SourceIndex - shift,
                            ["angle_deg"] = bend.AngleDeg,
                            ["fitting_id"] = fitting == null ? null : (JToken)RevitCompat.GetId(fitting.Id)
                        });
                    }

                    foreach (var target in targets)
                    {
                        var info = new JObject { ["at"] = target.End, ["element_id"] = target.ElementId };
                        if (target.Error != null)
                        {
                            info["reason"] = target.Error;
                            failed.Add(info);
                            continue;
                        }
                        var segment = target.End == "start" ? segments[0] : segments[segments.Count - 1];
                        var endPoint = Feet(target.End == "start" ? plan.Points[0] : plan.Points[plan.Points.Count - 1]);
                        var runEnd = ConnectorAt(segment, endPoint);
                        var made = new List<long>();
                        var how = "";
                        var ok = TryStep(doc, failed, requireConnections, info, () =>
                        {
                            how = Join(doc, kind, target, runEnd, made);
                            return null;
                        }, () => runEnd.IsConnected);
                        if (!ok.Item2) continue;
                        if (target.Main != null)
                        {
                            var mainId = RevitCompat.GetId(target.Main.Id);
                            modifiedIds.Add(mainId);
                            if (mainId != target.ElementId)
                                joined.Add(new JObject { ["at"] = target.End, ["requested_id"] = target.ElementId, ["element_id"] = mainId });
                        }
                        fittingIds.AddRange(made.Where(id => doc.GetElement(RevitCompat.ToElementId(id)) is FamilyInstance));
                        createdIds.AddRange(made);
                        connections.Add(new JObject
                        {
                            ["at"] = target.End,
                            ["element_id"] = target.ElementId,
                            ["connector_id"] = target.Connector?.Id,
                            ["how"] = how,
                            ["created_ids"] = new JArray(made)
                        });
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures. Nothing was created.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the " + kind.Name + " run: " + ex.Message + " Nothing was created.",
                        failed.Count > 0 ? new JObject { ["failed_connections"] = failed } : null);
                }
            }

            var first = segments[0];
            return CommandResult.Ok(new
            {
                kind = kindName,
                type = curveType.Name,
                type_id = RevitCompat.GetId(curveType.Id),
                system_type = systemType?.Name,
                system_type_id = systemType == null ? (long?)null : RevitCompat.GetId(systemType.Id),
                level = level.Name,
                segment_ids = segments.Select(s => RevitCompat.GetId(s.Id)).ToArray(),
                fitting_ids = fittingIds,
                created_ids = createdIds,
                modified_ids = modifiedIds,
                bends,
                connections,
                failed_connections = failed,
                joined,
                dropped_point_indices = plan.Dropped.Select(i => i - shift).Where(i => i >= 0).ToArray(),
                length_mm = Math.Round(segments.Sum(s => MepHelpers.ReadLengthMm(s, BuiltInParameter.CURVE_ELEM_LENGTH) ?? 0), 1),
                diameter_mm = MepHelpers.ReadLengthMm(first, kind.Name == "pipe" ? BuiltInParameter.RBS_PIPE_DIAMETER_PARAM
                    : kind.Name == "conduit" ? BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM : BuiltInParameter.RBS_CURVE_DIAMETER_PARAM),
                width_mm = MepHelpers.ReadLengthMm(first, kind.Name == "cable tray" ? BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM : BuiltInParameter.RBS_CURVE_WIDTH_PARAM),
                height_mm = MepHelpers.ReadLengthMm(first, kind.Name == "cable tray" ? BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM : BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)
            });
        }

        private static string SystemClass(MEPSystemType type)
        {
            try { return type.SystemClassification.ToString(); }
            catch { return null; }
        }

        private static bool TryPoints(JToken token, out List<double[]> points, out CommandResult failure)
        {
            points = new List<double[]>();
            failure = null;
            if (!(token is JArray array) || array.Count < 2 || array.Count > MaxPoints)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: points is a list of 2 to " + MaxPoints + " points, each [x, y, z] in mm.");
                return false;
            }
            for (var i = 0; i < array.Count; i++)
            {
                if (!(array[i] is JArray xyz) || xyz.Count != 3
                    || xyz.Any(v => v.Type != JTokenType.Float && v.Type != JTokenType.Integer))
                {
                    failure = CommandResult.Fail("INVALID_PARAMS: points[" + i + "] must be [x, y, z], numbers of mm.");
                    return false;
                }
                points.Add(xyz.Select(v => v.Value<double>()).ToArray());
            }
            return true;
        }

        private static bool TryCurveType(Document doc, Kind kind, JObject p, out ElementType type, out CommandResult failure)
        {
            type = null;
            var id = p.Value<long?>("type_id");
            var name = p.Value<string>("type_name");
            bool found;
            switch (kind.Name)
            {
                case "pipe": found = MepHelpers.TryType(doc, id, name, "pipe", "type_id", "type_name", out PipeType pipe, out failure); type = pipe; break;
                case "duct": found = MepHelpers.TryType(doc, id, name, "duct", "type_id", "type_name", out DuctType duct, out failure); type = duct; break;
                case "conduit": found = MepHelpers.TryType(doc, id, name, "conduit", "type_id", "type_name", out ConduitType conduit, out failure); type = conduit; break;
                default: found = MepHelpers.TryType(doc, id, name, "cable tray", "type_id", "type_name", out CableTrayType tray, out failure); type = tray; break;
            }
            return found;
        }

        private static MEPCurve CreateSegment(Document doc, Kind kind, ElementType type, MEPSystemType systemType, Level level, XYZ a, XYZ b)
        {
            MEPCurve curve;
            switch (kind.Name)
            {
                case "pipe": curve = Pipe.Create(doc, systemType.Id, type.Id, level.Id, a, b); break;
                case "duct": curve = Duct.Create(doc, systemType.Id, type.Id, level.Id, a, b); break;
                case "conduit": curve = Conduit.Create(doc, type.Id, a, b, level.Id); break;
                default: curve = CableTray.Create(doc, type.Id, a, b, level.Id); break;
            }
            return curve ?? throw new InvalidOperationException("Revit returned no " + kind.Name + ".");
        }

        /// <summary>Works out, before anything is drawn, what an end joins and where the run must reach.</summary>
        private static EndTarget ResolveTarget(Document doc, Kind kind, string runClass, string end, long elementId, int? connectorId, XYZ near)
        {
            var target = new EndTarget { End = end, ElementId = elementId };
            var element = doc.GetElement(RevitCompat.ToElementId(elementId));
            if (element == null)
            {
                target.Error = "ELEMENT_NOT_FOUND: " + end + "_connect element " + elementId + " is not in this document.";
                return target;
            }
            target.Element = element;
            if (element is MEPCurve && element.GetType() != kind.CurveClass)
            {
                target.Error = "WRONG_KIND: element " + elementId + " is a " + element.GetType().Name + "; a " + kind.Name + " run joins a " + kind.Name + ".";
                return target;
            }
            var manager = MepHelpers.GetConnectorManager(element);
            if (manager == null)
            {
                target.Error = "NO_CONNECTORS: element " + elementId + " has no MEP connectors.";
                return target;
            }

            var candidates = new List<Tuple<int, double, Connector>>();
            var reasons = new List<string>();
            foreach (Connector c in manager.Connectors)
            {
                if (connectorId.HasValue && c.Id != connectorId.Value) continue;
                if (c.ConnectorType != ConnectorType.End) { reasons.Add(c.Id + ": not an end connector"); continue; }
                if (c.Domain != kind.Domain) { reasons.Add(c.Id + ": " + MepHelpers.DomainName(c.Domain) + ", not " + MepHelpers.DomainName(kind.Domain)); continue; }
                if (c.IsConnected) { reasons.Add(c.Id + ": already connected"); continue; }
                var rank = ConnectorMatch.Rank(ConnectorClass(c), runClass);
                if (rank == ConnectorMatch.Incompatible) { reasons.Add(c.Id + ": system " + ConnectorClass(c) + ", run is " + runClass); continue; }
                if (element is MEPCurve && c.Origin.DistanceTo(near) > Tolerance) { reasons.Add(c.Id + ": the curve's free end is not at the run's " + end); continue; }
                candidates.Add(Tuple.Create(rank, c.Origin.DistanceTo(near), c));
            }
            if (candidates.Count > 0)
            {
                target.Connector = candidates.OrderBy(t => t.Item1).ThenBy(t => t.Item2).First().Item3;
                return target;
            }

            if (element is MEPCurve main && !connectorId.HasValue)
            {
                if (!kind.CanBreak)
                {
                    target.Error = "TEE_UNSUPPORTED: Revit's API breaks only pipes and ducts; join a " + kind.Name + " run at a free end.";
                    return target;
                }
                var line = (main.Location as LocationCurve)?.Curve as Line;
                if (line == null)
                {
                    target.Error = "MAIN_NOT_STRAIGHT: element " + elementId + " is not a straight " + kind.Name + ".";
                    return target;
                }
                var nearMm = PointMm(near);
                // The given curve first, as before (no limit on how far beside it the point is); else the main's network.
                if (MainSegments.Offset(nearMm, PointMm(line.GetEndPoint(0)), PointMm(line.GetEndPoint(1)), TeeMarginMm) == null)
                {
                    var network = MainNetwork(main, kind);
                    var lines = network.Select(c => (Line)((LocationCurve)c.Location).Curve).ToList();
                    var index = MainSegments.Containing(nearMm,
                        lines.Select(l => new[] { PointMm(l.GetEndPoint(0)), PointMm(l.GetEndPoint(1)) }).ToList(), TeeMarginMm, MaxTeeOffsetMm);
                    if (index < 0)
                    {
                        target.Error = "OFF_THE_MAIN: the run's " + end + " is not beside the middle of " + kind.Name + " " + elementId
                            + " nor of any straight " + kind.Name + " connected to it (" + network.Count + " searched, through fittings and "
                            + "accessories; a tee needs " + TeeMarginMm + " mm of main each side and the point within " + MaxTeeOffsetMm
                            + " mm of the main's line).";
                        return target;
                    }
                    main = network[index];
                    line = lines[index];
                }
                var t = (near - line.GetEndPoint(0)).DotProduct(line.Direction);
                target.Main = main;
                target.Point = line.GetEndPoint(0) + line.Direction * t;
                return target;
            }

            target.Error = "NO_FREE_CONNECTOR: element " + elementId + " has no free " + MepHelpers.DomainName(kind.Domain) + " connector"
                + (runClass == null ? "" : " for a " + runClass + " system") + (reasons.Count > 0 ? " (" + string.Join("; ", reasons) + ")" : "") + ".";
            return target;
        }

        /// <summary>Joins the run's end to the target; returns how. Adds every element it creates to made.</summary>
        private static string Join(Document doc, Kind kind, EndTarget target, Connector runEnd, List<long> made)
        {
            if (target.Main != null)
            {
                var newId = kind.Name == "pipe"
                    ? PlumbingUtils.BreakCurve(doc, target.Main.Id, target.Point)
                    : MechanicalUtils.BreakCurve(doc, target.Main.Id, target.Point);
                made.Add(RevitCompat.GetId(newId));
                var piece = (MEPCurve)doc.GetElement(newId);
                var tee = doc.Create.NewTeeFitting(ConnectorAt(target.Main, target.Point), ConnectorAt(piece, target.Point), runEnd)
                    ?? throw new InvalidOperationException("Revit returned no tee.");
                made.Add(RevitCompat.GetId(tee.Id));
                return "tee";
            }
            var other = target.Connector;
            if (other.Owner is MEPCurve)
            {
                var a = other.CoordinateSystem.BasisZ;
                var b = runEnd.CoordinateSystem.BasisZ;
                if (a.DotProduct(b) > -0.9999)
                {
                    var elbow = doc.Create.NewElbowFitting(other, runEnd) ?? throw new InvalidOperationException("Revit returned no elbow.");
                    made.Add(RevitCompat.GetId(elbow.Id));
                    return "elbow";
                }
            }
            runEnd.ConnectTo(other);
            return "connector";
        }

        /// <summary>
        /// Runs one fitting or join in a sub-transaction. On failure it is undone and listed in failed (or, when connections are
        /// required, the whole run fails). Returns the fitting made, if any, and whether the step succeeded.
        /// </summary>
        private static Tuple<FamilyInstance, bool> TryStep(Document doc, JArray failed, bool require, JObject info, Func<FamilyInstance> step,
            Func<bool> check)
        {
            using (var sub = new SubTransaction(doc))
            {
                sub.Start();
                try
                {
                    var made = step();
                    doc.Regenerate();
                    if (check != null && !check()) throw new InvalidOperationException("Revit did not connect the run's end.");
                    sub.Commit();
                    return Tuple.Create(made, true);
                }
                catch (Exception ex)
                {
                    if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                    var reason = ex.Message;
                    if (require) throw new InvalidOperationException("joining at the " + info.Value<string>("at") + " failed: " + reason);
                    info["reason"] = "JOIN_FAILED: " + reason;
                    failed.Add(info);
                    return Tuple.Create<FamilyInstance, bool>(null, false);
                }
            }
        }

        /// <summary>The curve's end connector nearest the point.</summary>
        private static Connector ConnectorAt(MEPCurve curve, XYZ point)
        {
            Connector best = null;
            var bestDistance = double.MaxValue;
            foreach (Connector c in curve.ConnectorManager.Connectors)
            {
                if (c.ConnectorType != ConnectorType.End) continue;
                var d = c.Origin.DistanceTo(point);
                if (d < bestDistance) { best = c; bestDistance = d; }
            }
            if (best == null || bestDistance > Tolerance)
                throw new InvalidOperationException("no end connector of " + RevitCompat.GetId(curve.Id) + " lies at the joint.");
            return best;
        }

        /// <summary>The connector's system classification name, or null when it has none.</summary>
        private static string ConnectorClass(Connector c)
        {
            try
            {
                if (c.Domain == Domain.DomainPiping) return c.PipeSystemType.ToString();
                if (c.Domain == Domain.DomainHvac) return c.DuctSystemType.ToString();
            }
            catch
            {
                // A connector without a readable classification takes any system.
            }
            return null;
        }

        /// <summary>
        /// The straight curves of the run's kind connected to the main, the main first: walked through their connectors, and
        /// through fittings (elbows, tees, couplings, unions...) and in-line accessories (valves), never through fixtures or
        /// equipment; a curve on another MEP system than the main's is not followed. Bounded: at most MaxNetworkElements
        /// elements are visited.
        /// </summary>
        private static List<MEPCurve> MainNetwork(MEPCurve main, Kind kind)
        {
            var systemId = main.MEPSystem?.Id;
            var curves = new List<MEPCurve>();
            var visited = new HashSet<long> { RevitCompat.GetId(main.Id) };
            var queue = new Queue<Element>();
            queue.Enqueue(main);
            while (queue.Count > 0)
            {
                var element = queue.Dequeue();
                if (element is MEPCurve curve && (curve.Location as LocationCurve)?.Curve is Line) curves.Add(curve);
                var manager = MepHelpers.GetConnectorManager(element);
                if (manager == null) continue;
                foreach (Connector c in manager.Connectors)
                {
                    if (c.Domain != kind.Domain || !c.IsConnected) continue;
                    foreach (Connector other in c.AllRefs)
                    {
                        if (visited.Count >= MaxNetworkElements) return curves;
                        if (other.ConnectorType == ConnectorType.Logical) continue;
                        var owner = other.Owner;
                        if (owner == null || !visited.Add(RevitCompat.GetId(owner.Id))) continue;
                        if (owner is MEPCurve next)
                        {
                            if (next.GetType() != kind.CurveClass) continue;
                            if (systemId != null && next.MEPSystem != null && next.MEPSystem.Id != systemId) continue;
                            queue.Enqueue(next);
                        }
                        else if (owner is FamilyInstance && IsInLine(owner)) queue.Enqueue(owner);
                    }
                }
            }
            return curves;
        }

        /// <summary>A fitting or an accessory: the main runs on through it.</summary>
        private static bool IsInLine(Element element)
        {
            var category = element.Category?.BuiltInCategory;
            return category == BuiltInCategory.OST_PipeFitting || category == BuiltInCategory.OST_DuctFitting
                || category == BuiltInCategory.OST_PipeAccessory || category == BuiltInCategory.OST_DuctAccessory;
        }

        private static double[] PointMm(XYZ p) => new[] { Mm.FromFeet(p.X, 3), Mm.FromFeet(p.Y, 3), Mm.FromFeet(p.Z, 3) };

        private static XYZ Feet(double[] mm) => new XYZ(Mm.ToFeet(mm[0]), Mm.ToFeet(mm[1]), Mm.ToFeet(mm[2]));
    }
}
