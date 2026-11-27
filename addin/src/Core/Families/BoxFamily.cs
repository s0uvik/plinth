// New in Plinth (Step 10, fit-out, 2026-11-17): the Revit-free parts of create_box_family. When no family suits a fit-out
// item and the user says so (their loaded bed set was 3.5 m wide and fitted no bedroom), Plinth makes a plain box family
// for it: one extrusion W x D x H with type parameters Width, Depth and Height and a Material parameter, its origin at
// the middle of its back edge (back on +Y, front facing -Y, the fit-out's convention), its base at the level. Names,
// sizes, the template per category and the box about its origin are decided here, so they are unit-tested.
// Connectors (2026-11-27, MEP fit-out on an indicative coordination model): a box may carry electrical and pipe connectors,
// each at the centre of one of its faces (back = +Y, front = -Y, left = -X, right = +X, top, bottom); several on one face
// are spread evenly along it (along the width for back, front, top and bottom, along the depth for left and right) so they
// never coincide. Which connectors are valid and where they sit are decided here.
// Power connectors (2026-11-27, after a Revit API review): the family editor offers only Power - Balanced and Power -
// Unbalanced for a power connector, so a box makes PowerBalanced when PowerCircuit is asked or system_type is omitted
// (PowerCircuit stays an accepted name, since callers think in circuits) and reports the type it made. voltage_v (0 < v <=
// 1000, power connectors only) sets the connector's voltage; without it Revit's default voltage is left as it is.
// The flex check asks OnFace whether each connector is still on its face (on its plane and inside its edges) at the flexed
// size.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Core.Families
{
    /// <summary>One connector asked of a box family, validated (names are Revit's enum names, exactly cased).</summary>
    public sealed class BoxConnector
    {
        public const string Electrical = "electrical";
        public const string Pipe = "pipe";

        /// <summary>electrical or pipe.</summary>
        public string Domain { get; set; }

        /// <summary>electrical: an ElectricalSystemType name; pipe: a PipeSystemType name (its classification).</summary>
        public string SystemType { get; set; }

        /// <summary>pipe: the connector's diameter in mm; 0 for electrical.</summary>
        public double DiameterMm { get; set; }

        /// <summary>electrical (power only): the voltage to set, in volts; null leaves Revit's default.</summary>
        public double? VoltageV { get; set; }

        /// <summary>back, front, left, right, top or bottom.</summary>
        public string Face { get; set; }
    }

    public static class BoxFamily
    {
        public const int MaxConnectors = 12;
        public const double MaxPipeDiameterMm = 600;
        public const double MaxVoltageV = 1000;

        /// <summary>The power connector type a box makes when system_type is omitted or PowerCircuit.</summary>
        public const string DefaultPowerConnector = "PowerBalanced";

        /// <summary>The power connector types; voltage_v is taken only for these.</summary>
        public static readonly IReadOnlyList<string> PowerConnectorTypes = new[] { "PowerBalanced", "PowerUnBalanced" };

        /// <summary>The faces a connector may sit on, in the order they are listed.</summary>
        public static readonly IReadOnlyList<string> Faces = new[] { "back", "front", "left", "right", "top", "bottom" };

        /// <summary>
        /// The ElectricalSystemType names a box connector's system_type takes (Revit's, less UndefinedSystemType);
        /// PowerCircuit is made as PowerBalanced.
        /// </summary>
        public static readonly IReadOnlyList<string> ElectricalSystemTypes = new[]
        {
            "PowerCircuit", "PowerBalanced", "PowerUnBalanced", "Data", "Communication", "Controls", "FireAlarm", "NurseCall",
            "Security", "Telephone"
        };

        /// <summary>The PipeSystemType names a box connector takes (Revit's, less Global, Fitting and UndefinedSystemType).</summary>
        public static readonly IReadOnlyList<string> PipeClassifications = new[]
        {
            "DomesticColdWater", "DomesticHotWater", "Sanitary", "Vent", "FireProtectWet", "FireProtectDry", "FireProtectPreaction",
            "FireProtectOther", "OtherPipe", "SupplyHydronic", "ReturnHydronic"
        };

        private static readonly string[] ElectricalKeys = { "domain", "system_type", "voltage_v", "face" };
        private static readonly string[] PipeKeys = { "domain", "classification", "diameter_mm", "face" };
        public const double MaxSizeMm = 20000;
        public const int MaxNameLength = 120;
        public const string TypeSuffix = "(Plinth)";

        /// <summary>The template used when the category has none of its own (the family category is then set).</summary>
        public const string GenericTemplate = "Metric Generic Model.rft";

        /// <summary>The English metric family template per built-in category (Revit's Family Templates\English folder).</summary>
        public static readonly IReadOnlyDictionary<string, string> CategoryTemplates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OST_Furniture"] = "Metric Furniture.rft",
            ["OST_FurnitureSystems"] = "Metric Furniture System.rft",
            ["OST_Casework"] = "Metric Casework.rft",
            ["OST_SpecialityEquipment"] = "Metric Specialty Equipment.rft",
            ["OST_PlumbingFixtures"] = "Metric Plumbing Fixture.rft",
            ["OST_MechanicalEquipment"] = "Metric Mechanical Equipment.rft",
            ["OST_ElectricalEquipment"] = "Metric Electrical Equipment.rft",
            ["OST_GenericModel"] = GenericTemplate,
        };

        // Characters Windows refuses in a file name (the family is saved as <name>.rfa and Revit names it after the file)
        // and those Revit refuses in a family name.
        private static readonly char[] Forbidden = "\\/:*?\"<>|{}[];`~".ToCharArray();

        /// <summary>Why the name cannot be a family name, or null when it can.</summary>
        public static string NameError(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "the family name is empty";
            if (name != name.Trim()) return "the family name starts or ends with a space";
            if (name.Length > MaxNameLength) return "the family name is longer than " + MaxNameLength + " characters";
            if (name.EndsWith(".", StringComparison.Ordinal)) return "the family name ends with a dot";
            var bad = name.Where(c => Array.IndexOf(Forbidden, c) >= 0 || char.IsControl(c)).Distinct().ToArray();
            return bad.Length == 0 ? null : "the family name contains " + string.Join(" ", bad.Select(c => "'" + c + "'"))
                + " (not allowed: \\ / : * ? \" < > | { } [ ] ; ` ~)";
        }

        /// <summary>Why the size cannot be built, or null: each of width, depth and height above 0 and at most 20 m.</summary>
        public static string SizeError(double widthMm, double depthMm, double heightMm)
        {
            foreach (var (label, value) in new[] { ("width_mm", widthMm), ("depth_mm", depthMm), ("height_mm", heightMm) })
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0 || value > MaxSizeMm)
                    return label + " must be a number of mm above 0 and at most " + MaxSizeMm.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        /// <summary>"1500 x 2000 x 450mm (Plinth)": width, depth, height.</summary>
        public static string DefaultTypeName(double widthMm, double depthMm, double heightMm) =>
            Number(widthMm) + " x " + Number(depthMm) + " x " + Number(heightMm) + "mm " + TypeSuffix;

        public static string Number(double mm)
        {
            var rounded = Math.Round(mm, 1);
            return rounded.ToString(Math.Abs(rounded - Math.Round(rounded)) < 1e-9 ? "0" : "0.0", CultureInfo.InvariantCulture);
        }

        /// <summary>The templates to try for the category, in order: its own (if it has one), then the generic model.</summary>
        public static IReadOnlyList<string> Templates(string builtInCategory)
        {
            var list = new List<string>();
            if (builtInCategory != null && CategoryTemplates.TryGetValue(builtInCategory, out var own)) list.Add(own);
            if (!list.Contains(GenericTemplate)) list.Add(GenericTemplate);
            return list;
        }

        /// <summary>
        /// The box about the family's origin (mm), min then max: x across its width, centred; y from its front (-depth) to
        /// its back (0); z from the level (0) up.
        /// </summary>
        public static double[][] Box(double widthMm, double depthMm, double heightMm) => new[]
        {
            new[] { -widthMm / 2, -depthMm, 0.0 },
            new[] { widthMm / 2, 0.0, heightMm },
        };

        /// <summary>
        /// Why the connectors param cannot be built, or null; connectors gets the validated list (empty when the param is
        /// absent or null). Each item is {domain: electrical, system_type?: PowerBalanced, voltage_v?: 230, face?: back} or
        /// {domain: pipe, classification: DomesticColdWater..., diameter_mm: > 0, face?: back}; names match case-insensitively
        /// and come back as Revit spells them, an electrical system_type as the type made (PowerCircuit or none: PowerBalanced);
        /// an unknown key is an error, so a typo is never silently ignored.
        /// </summary>
        public static string ConnectorsError(JToken token, out List<BoxConnector> connectors)
        {
            connectors = new List<BoxConnector>();
            if (token == null || token.Type == JTokenType.Null) return null;
            if (!(token is JArray array)) return "connectors must be a list of {domain, ...} objects";
            if (array.Count > MaxConnectors) return "connectors lists at most " + MaxConnectors + " connectors";
            for (var i = 0; i < array.Count; i++)
            {
                var at = "connectors[" + i + "]";
                if (!(array[i] is JObject item)) return at + " must be an object {domain, ...}";
                var domain = Text(item["domain"])?.Trim().ToLowerInvariant();
                if (domain != BoxConnector.Electrical && domain != BoxConnector.Pipe)
                    return at + ".domain must be electrical or pipe";
                var allowed = domain == BoxConnector.Electrical ? ElectricalKeys : PipeKeys;
                var unknown = item.Properties().Select(x => x.Name).Where(n => !allowed.Contains(n)).ToList();
                if (unknown.Count > 0)
                    return at + " (" + domain + ") does not take " + string.Join(", ", unknown) + " (it takes " + string.Join(", ", allowed) + ")";

                var face = Faces[0];
                if (item["face"] != null && item["face"].Type != JTokenType.Null)
                {
                    face = Faces.FirstOrDefault(f => string.Equals(f, Text(item["face"])?.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (face == null) return at + ".face must be one of " + string.Join(", ", Faces);
                }

                var connector = new BoxConnector { Domain = domain, Face = face };
                if (domain == BoxConnector.Electrical)
                {
                    var given = item["system_type"] == null || item["system_type"].Type == JTokenType.Null ? DefaultPowerConnector : Text(item["system_type"]);
                    connector.SystemType = ElectricalConnectorType(Match(ElectricalSystemTypes, given));
                    if (connector.SystemType == null)
                        return at + ".system_type must be one of " + string.Join(", ", ElectricalSystemTypes);
                    var voltage = item["voltage_v"];
                    if (voltage != null && voltage.Type != JTokenType.Null)
                    {
                        var v = voltage.Type == JTokenType.Integer || voltage.Type == JTokenType.Float ? voltage.Value<double>() : double.NaN;
                        if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0 || v > MaxVoltageV)
                            return at + ".voltage_v must be a number of volts above 0 and at most " + MaxVoltageV.ToString(CultureInfo.InvariantCulture);
                        if (!PowerConnectorTypes.Contains(connector.SystemType))
                            return at + ".voltage_v is only for a power connector (PowerCircuit, PowerBalanced or PowerUnBalanced), not "
                                + connector.SystemType;
                        connector.VoltageV = v;
                    }
                }
                else
                {
                    connector.SystemType = Match(PipeClassifications, Text(item["classification"]));
                    if (connector.SystemType == null)
                        return at + ".classification must be one of " + string.Join(", ", PipeClassifications);
                    var diameter = item["diameter_mm"];
                    var mm = diameter != null && (diameter.Type == JTokenType.Integer || diameter.Type == JTokenType.Float)
                        ? diameter.Value<double>() : double.NaN;
                    if (double.IsNaN(mm) || double.IsInfinity(mm) || mm <= 0 || mm > MaxPipeDiameterMm)
                        return at + ".diameter_mm must be a number of mm above 0 and at most " + MaxPipeDiameterMm.ToString(CultureInfo.InvariantCulture);
                    connector.DiameterMm = mm;
                }
                connectors.Add(connector);
            }
            return null;
        }

        /// <summary>
        /// Where each connector sits about the family's origin (mm, the frame of Box): the centre of its face, or, when n
        /// connectors share a face, at (k + 1) / (n + 1) along it (the width for back, front, top and bottom; the depth for
        /// left and right), at the face's middle the other way. Listed in the connectors' order.
        /// </summary>
        public static List<double[]> ConnectorPositions(double widthMm, double depthMm, double heightMm, IReadOnlyList<BoxConnector> connectors)
        {
            var positions = new List<double[]>();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var connector in connectors)
            {
                var n = connectors.Count(c => c.Face == connector.Face);
                seen.TryGetValue(connector.Face, out var k);
                seen[connector.Face] = k + 1;
                var t = (k + 1.0) / (n + 1) - 0.5; // -0.5..0.5 along the face, 0 when it is alone
                double x = t * widthMm, yAlong = -depthMm / 2 + t * depthMm;
                switch (connector.Face)
                {
                    case "back": positions.Add(new[] { x, 0.0, heightMm / 2 }); break;
                    case "front": positions.Add(new[] { x, -depthMm, heightMm / 2 }); break;
                    case "left": positions.Add(new[] { -widthMm / 2, yAlong, heightMm / 2 }); break;
                    case "right": positions.Add(new[] { widthMm / 2, yAlong, heightMm / 2 }); break;
                    case "top": positions.Add(new[] { x, -depthMm / 2, heightMm }); break;
                    case "bottom": positions.Add(new[] { x, -depthMm / 2, 0.0 }); break;
                    default: throw new ArgumentException("unknown face " + connector.Face);
                }
            }
            return positions;
        }

        /// <summary>The outward normal of the face (x, y, z).</summary>
        public static double[] FaceNormal(string face)
        {
            switch (face)
            {
                case "back": return new[] { 0.0, 1.0, 0.0 };
                case "front": return new[] { 0.0, -1.0, 0.0 };
                case "left": return new[] { -1.0, 0.0, 0.0 };
                case "right": return new[] { 1.0, 0.0, 0.0 };
                case "top": return new[] { 0.0, 0.0, 1.0 };
                case "bottom": return new[] { 0.0, 0.0, -1.0 };
                default: throw new ArgumentException("unknown face " + face);
            }
        }

        /// <summary>
        /// Where the face's plane is about the origin (mm) on its axis (y for back and front, x for left and right, z for top
        /// and bottom): back 0, front -depth, left -width/2, right width/2, top height, bottom 0.
        /// </summary>
        public static double FaceCoordinate(string face, double widthMm, double depthMm, double heightMm)
        {
            switch (face)
            {
                case "back": return 0;
                case "front": return -depthMm;
                case "left": return -widthMm / 2;
                case "right": return widthMm / 2;
                case "top": return heightMm;
                case "bottom": return 0;
                default: throw new ArgumentException("unknown face " + face);
            }
        }

        /// <summary>The connector type a box makes for an asked ElectricalSystemType name: PowerCircuit becomes PowerBalanced.</summary>
        public static string ElectricalConnectorType(string asked) =>
            string.Equals(asked, "PowerCircuit", StringComparison.Ordinal) ? DefaultPowerConnector : asked;

        /// <summary>
        /// Why the point (mm about the origin) is not on the face of a box of this size, or null when it is: on the face's
        /// plane and inside its edges, within toleranceMm. The flex check asks this at the flexed size.
        /// </summary>
        public static string OnFace(string face, double[] pointMm, double widthMm, double depthMm, double heightMm, double toleranceMm)
        {
            var normal = FaceNormal(face);
            var axis = Array.FindIndex(normal, v => v != 0);
            var plane = FaceCoordinate(face, widthMm, depthMm, heightMm);
            if (Math.Abs(pointMm[axis] - plane) > toleranceMm)
                return "it is at " + Number(pointMm[axis]) + " mm along " + "xyz"[axis] + ", off the face's plane at " + Number(plane) + " mm";
            var box = Box(widthMm, depthMm, heightMm);
            for (var i = 0; i < 3; i++)
            {
                if (i == axis) continue;
                if (pointMm[i] < box[0][i] - toleranceMm || pointMm[i] > box[1][i] + toleranceMm)
                    return "it is at " + Number(pointMm[i]) + " mm along " + "xyz"[i] + ", outside the face's edges at " + Number(box[0][i])
                        + " to " + Number(box[1][i]) + " mm";
            }
            return null;
        }

        private static string Text(JToken token) => token != null && token.Type == JTokenType.String ? token.Value<string>() : null;

        private static string Match(IEnumerable<string> names, string given) =>
            given == null ? null : names.FirstOrDefault(n => string.Equals(n, given.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
