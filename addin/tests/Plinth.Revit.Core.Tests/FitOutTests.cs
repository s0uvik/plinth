using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Plinth.Revit.Core.Execution;
using Plinth.Revit.Core.Families;
using Plinth.Revit.Core.Mep;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Plinth.Revit.Core.Tests
{
    /// <summary>Revit-free parts of the Step 10 fit-out commands (list/copy template types, family placement, create_mep_run).</summary>
    public class FitOutTests
    {
        private static double[] P(double x, double y, double z) => new[] { x, y, z };

        // ---- RunPath ----------------------------------------------------------------------------------

        [Fact]
        public void RunWithSquareBendsKeepsEveryPointAndListsBends()
        {
            var plan = RunPath.Build(new List<double[]> { P(0, 0, 0), P(1000, 0, 0), P(1000, 1000, 0), P(1000, 1000, -500) }, 0.8);
            Assert.Null(plan.Error);
            Assert.Equal(3, plan.SegmentCount);
            Assert.Equal(new[] { 1, 2 }, plan.Bends.Select(b => b.Index));
            Assert.All(plan.Bends, b => Assert.Equal(90, b.AngleDeg, 3));
        }

        [Fact]
        public void PointsOnAStraightLineAreDropped()
        {
            var plan = RunPath.Build(new List<double[]> { P(0, 0, 0), P(500, 0, 0), P(1000, 0, 0), P(1000, 800, 0) }, 0.8);
            Assert.Null(plan.Error);
            Assert.Equal(new[] { 1 }, plan.Dropped);
            Assert.Equal(new[] { 0, 2, 3 }, plan.SourceIndices);
            Assert.Single(plan.Bends);
            Assert.Equal(2, plan.Bends[0].SourceIndex);
        }

        [Fact]
        public void SlopedDrainKeepsItsGentleBend()
        {
            // 1:40 fall then a drop: the first joint is a real (small) bend, not straight.
            var plan = RunPath.Build(new List<double[]> { P(0, 0, 0), P(2000, 0, -50), P(2000, 0, -1000) }, 0.8);
            Assert.Null(plan.Error);
            Assert.Single(plan.Bends);
            Assert.InRange(plan.Bends[0].AngleDeg, 88, 92);
        }

        [Fact]
        public void BadRunsAreRefused()
        {
            Assert.Contains("at least 2 points", RunPath.Build(new List<double[]> { P(0, 0, 0) }, 0.8).Error);
            Assert.Contains("points[1]", RunPath.Build(new List<double[]> { P(0, 0, 0), new[] { 1.0, 2.0 } }, 0.8).Error);
            Assert.Contains("repeated", RunPath.Build(new List<double[]> { P(0, 0, 0), P(0.5, 0, 0) }, 0.8).Error);
            Assert.Contains("turns back", RunPath.Build(new List<double[]> { P(0, 0, 0), P(1000, 0, 0), P(0, 0, 0.001) }, 0.8).Error);
            Assert.Contains("finite", RunPath.Build(new List<double[]> { P(0, 0, 0), P(double.NaN, 0, 0) }, 0.8).Error);
        }

        [Fact]
        public void TwoPointRunHasNoBends()
        {
            var plan = RunPath.Build(new List<double[]> { P(0, 0, 0), P(0, 0, 3000) }, 0.8);
            Assert.Null(plan.Error);
            Assert.Equal(1, plan.SegmentCount);
            Assert.Empty(plan.Bends);
        }

        [Fact]
        public void RunReachesItsTargetBySnappingOrExtending()
        {
            var points = new List<double[]> { P(0, 0, 0), P(1000, 0, 0) };
            Assert.Equal("snapped", RunPath.Reach(points, false, P(1000.5, 0, 0), 1.0));
            Assert.Equal(1000.5, points[1][0]);
            Assert.Equal("extended", RunPath.Reach(points, true, P(0, 0, -400), 1.0));
            Assert.Equal(3, points.Count);
            Assert.Equal(-400, points[0][2]);
            Assert.Equal("extended", RunPath.Reach(points, false, P(1500, 0, 0), 1.0));
            var plan = RunPath.Build(points, 0.8);
            Assert.Equal(new[] { 2 }, plan.Dropped); // Straight on: the old end is merged into one segment.
            Assert.Single(plan.Bends);
        }

        // ---- ConnectorMatch ---------------------------------------------------------------------------

        [Theory]
        [InlineData("Sanitary", "Sanitary", ConnectorMatch.Exact)]
        [InlineData("DomesticColdWater", "Sanitary", ConnectorMatch.Incompatible)]
        [InlineData("Global", "Sanitary", ConnectorMatch.Any)]
        [InlineData("Fitting", "DomesticHotWater", ConnectorMatch.Any)]
        [InlineData("UndefinedSystemType", "SupplyAir", ConnectorMatch.Any)]
        [InlineData("SupplyAir", null, ConnectorMatch.Any)]
        [InlineData(null, "ExhaustAir", ConnectorMatch.Any)]
        public void ConnectorClassesMatchByName(string connector, string run, int expected)
        {
            Assert.Equal(expected, ConnectorMatch.Rank(connector, run));
        }

        // ---- MainSegments -----------------------------------------------------------------------------

        private static double[][] Seg(double[] a, double[] b) => new[] { a, b };

        [Fact]
        public void ATeeGoesIntoTheSegmentOfTheMainItsPointLiesBeside()
        {
            // A 5-segment cold-water downtake: along x at z 3000 to x 4000, down to z 0, then along y.
            var main = new List<double[][]>
            {
                Seg(P(0, 0, 3000), P(2000, 0, 3000)),
                Seg(P(2000, 0, 3000), P(4000, 0, 3000)),
                Seg(P(4000, 0, 3000), P(4000, 0, 0)),
                Seg(P(4000, 0, 0), P(4000, 1500, 0)),
                Seg(P(4000, 1500, 0), P(4000, 3000, 0)),
            };
            Assert.Equal(3, MainSegments.Containing(P(4000, 900, 0), main, 50, 300));          // on segment 4
            Assert.Equal(1, MainSegments.Containing(P(3000, 0, 3000), main, 50, 300));
            Assert.Equal(2, MainSegments.Containing(P(4000, 120, 1500), main, 50, 300));      // 120 mm beside it
            Assert.Equal(-1, MainSegments.Containing(P(4000, 400, 1500), main, 50, 300));     // too far beside
            Assert.Equal(-1, MainSegments.Containing(P(1980, 0, 3000), main, 50, 300));       // 20 mm from a joint
            Assert.Equal(-1, MainSegments.Containing(P(-100, 0, 3000), main, 50, 300));       // past the main's end
            Assert.Equal(4, MainSegments.Containing(P(4000, 3000 - 50, 0), main, 50, 300));   // exactly the margin
        }

        [Fact]
        public void TheNearestSegmentWinsAndTheFirstOnATie()
        {
            var parallel = new List<double[][]>
            {
                Seg(P(0, 200, 0), P(1000, 200, 0)),
                Seg(P(0, 50, 0), P(1000, 50, 0)),
                Seg(P(0, -50, 0), P(1000, -50, 0)),
            };
            Assert.Equal(1, MainSegments.Containing(P(500, 0, 0), parallel, 50, 300));        // y 50 and y -50 tie: the first
            Assert.Equal(1, MainSegments.Containing(P(500, 10, 0), parallel, 50, 300));       // nearer y 50
            Assert.Equal(2, MainSegments.Containing(P(500, -10, 0), parallel, 50, 300));      // nearer y -50
            Assert.Equal(0, MainSegments.Containing(P(500, 190, 0), parallel, 50, 300));
            Assert.Equal(-1, MainSegments.Containing(P(500, 0, 0), new List<double[][]>(), 50, 300));
        }

        [Fact]
        public void ASegmentTooShortForATeeTakesNoPoint()
        {
            Assert.Null(MainSegments.Offset(P(40, 0, 0), P(0, 0, 0), P(80, 0, 0), 50));
            Assert.Null(MainSegments.Offset(P(0, 0, 0), P(0, 0, 0), P(0, 0, 0), 50));
            Assert.Equal(0.0, MainSegments.Offset(P(50, 0, 0), P(0, 0, 0), P(100, 0, 0), 50).Value, 9);
            Assert.Equal(5.0, MainSegments.Offset(P(500, 3, 4), P(0, 0, 0), P(1000, 0, 0), 50).Value, 9);
        }

        // ---- CircuitTypes -----------------------------------------------------------------------------

        [Theory]
        [InlineData(null, "PowerCircuit")]
        [InlineData("PowerCircuit", "PowerCircuit")]
        [InlineData("firealarm", "FireAlarm")]
        [InlineData(" Data ", "Data")]
        [InlineData("Communication", "Communication")]
        [InlineData("Controls", "Controls")]
        [InlineData("NurseCall", "NurseCall")]
        [InlineData("Security", "Security")]
        [InlineData("telephone", "Telephone")]
        public void CircuitTypesAreTheEightRevitMakes(string given, string expected)
        {
            Assert.Null(CircuitTypes.Error(given, out var type));
            Assert.Equal(expected, type);
        }

        [Theory]
        [InlineData("PowerBalanced")]
        [InlineData("PowerUnBalanced")]
        [InlineData("UndefinedSystemType")]
        [InlineData("Lighting")]
        [InlineData("")]
        [InlineData("1")]
        public void ConnectorTypesAndOthersAreNotCircuitTypes(string given)
        {
            var error = CircuitTypes.Error(given, out var type);
            Assert.Null(type);
            Assert.Contains("is not a circuit type; use one of PowerCircuit, Data, Communication, Controls, FireAlarm, NurseCall, Security, Telephone", error);
        }

        [Theory]
        [InlineData("PowerCircuit", "PowerBalanced", CircuitTypes.Joins)]
        [InlineData("PowerCircuit", "PowerUnBalanced", CircuitTypes.Joins)]
        [InlineData("PowerCircuit", "PowerCircuit", CircuitTypes.Joins)]
        [InlineData("PowerCircuit", "Data", CircuitTypes.Refused)]
        [InlineData("PowerCircuit", "UndefinedSystemType", CircuitTypes.RevitDecides)]
        [InlineData("PowerCircuit", null, CircuitTypes.RevitDecides)]
        [InlineData("Data", "Data", CircuitTypes.Joins)]
        [InlineData("Data", "PowerBalanced", CircuitTypes.Refused)]
        [InlineData("FireAlarm", "Security", CircuitTypes.Refused)]
        [InlineData("FireAlarm", "UndefinedSystemType", CircuitTypes.RevitDecides)]
        [InlineData("Telephone", null, CircuitTypes.RevitDecides)]
        public void ConnectorsFitTheirCircuitType(string circuit, string connector, int expected)
        {
            Assert.Equal(expected, CircuitTypes.Fit(circuit, connector));
        }

        // ---- TemplateFiles ----------------------------------------------------------------------------

        [Fact]
        public void TemplatesAreListedPerLanguageFolder()
        {
            var root = Path.Combine(Path.GetTempPath(), "plinth-templates-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "English"));
                Directory.CreateDirectory(Path.Combine(root, "French"));
                File.WriteAllText(Path.Combine(root, "Default_M_ENU.rte"), "x");
                File.WriteAllText(Path.Combine(root, "English", "Systems-Default_Metric.rte"), "xx");
                File.WriteAllText(Path.Combine(root, "English", "DefaultMetric.rte"), "x");
                File.WriteAllText(Path.Combine(root, "English", "readme.txt"), "x");
                File.WriteAllText(Path.Combine(root, "French", "DefaultFRA.rte"), "x");

                var all = TemplateFiles.List(root);
                Assert.Equal(new[] { "Default_M_ENU.rte", "DefaultMetric.rte", "Systems-Default_Metric.rte", "DefaultFRA.rte" }, all.Select(e => e.Name));
                Assert.Equal("", all[0].Language);
                Assert.Equal(2, all[2].SizeBytes);

                var english = TemplateFiles.List(root, "english");
                Assert.Equal(2, english.Count);
                Assert.All(english, e => Assert.Equal("English", e.Language));

                Assert.Empty(TemplateFiles.List(Path.Combine(root, "missing")));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void TemplateRootAndFileKinds()
        {
            Assert.Equal(Path.Combine(@"C:\ProgramData", "Autodesk", "RVT 2026", "Templates"), TemplateFiles.Root(@"C:\ProgramData", "2026"));
            Assert.True(TemplateFiles.IsProjectFile(@"C:\a\b.RTE"));
            Assert.True(TemplateFiles.IsProjectFile(@"C:\a\b.rvt"));
            Assert.False(TemplateFiles.IsProjectFile(@"C:\a\b.rfa"));
            Assert.False(TemplateFiles.IsProjectFile(null));
        }

        // ---- TypeListCache ----------------------------------------------------------------------------

        [Fact]
        public void CacheKeyChangesWithFileYearAndPathCase()
        {
            var when = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var key = TypeListCache.Key(@"C:\T\Systems.rte", 100, when, "2026");
            Assert.Equal(key, TypeListCache.Key(@"c:\t\systems.rte", 100, when, "2026"));
            Assert.NotEqual(key, TypeListCache.Key(@"C:\T\Systems.rte", 101, when, "2026"));
            Assert.NotEqual(key, TypeListCache.Key(@"C:\T\Systems.rte", 100, when.AddSeconds(1), "2026"));
            Assert.NotEqual(key, TypeListCache.Key(@"C:\T\Systems.rte", 100, when, "2027"));
        }

        [Fact]
        public void CacheRoundTripsAndIgnoresBadFiles()
        {
            var folder = Path.Combine(Path.GetTempPath(), "plinth-cache-" + Guid.NewGuid().ToString("N"));
            try
            {
                Assert.Null(TypeListCache.TryRead(folder, "k"));
                Assert.True(TypeListCache.Write(folder, "k", new JObject { ["family_types"] = new JArray(1, 2) }));
                var read = TypeListCache.TryRead(folder, "k");
                Assert.Equal(2, ((JArray)read["family_types"]).Count);
                Assert.True(TypeListCache.Write(folder, "k", new JObject { ["family_types"] = new JArray(3) }));
                Assert.Single((JArray)TypeListCache.TryRead(folder, "k")["family_types"]);

                File.WriteAllText(TypeListCache.PathFor(folder, "bad"), "{not json");
                Assert.Null(TypeListCache.TryRead(folder, "bad"));
                File.WriteAllText(TypeListCache.PathFor(folder, "old"), "{\"format_version\":0}");
                Assert.Null(TypeListCache.TryRead(folder, "old"));
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        // ---- batch refusals ---------------------------------------------------------------------------

        [Theory]
        [InlineData("list_types_in_file")]
        [InlineData("copy_types_from_file")]
        public void FileOpeningCommandsAreRefusedInABatch(string name)
        {
            var outcome = BatchExecutor.Precheck(new JArray(new JObject { ["command"] = name }), _ => "{}");
            Assert.Contains("NOT_ALLOWED_IN_BATCH", outcome.FirstError);
        }

        // ---- PlacementRules ---------------------------------------------------------------------------

        private static PlacementRules.Mode? Choose(string type, PlacementRules.HostKind host, PlacementRules.Options o, out string error) =>
            PlacementRules.Choose(type, host, o, out error, out _);

        [Fact]
        public void PlacementModesFollowFamilyAndHost()
        {
            var none = new PlacementRules.Options();
            var side = new PlacementRules.Options { HasSide = true };
            Assert.Equal(PlacementRules.Mode.Level, Choose("OneLevelBased", PlacementRules.HostKind.None, new PlacementRules.Options { HasRotation = true, HasHeight = true }, out _));
            Assert.Equal(PlacementRules.Mode.WallHosted, Choose("OneLevelBasedHosted", PlacementRules.HostKind.Wall, side, out _));
            Assert.Equal(PlacementRules.Mode.Hosted, Choose("OneLevelBasedHosted", PlacementRules.HostKind.Horizontal, none, out _));
            Assert.Equal(PlacementRules.Mode.WallFace, Choose("WorkPlaneBased", PlacementRules.HostKind.Wall, side, out _));
            Assert.Equal(PlacementRules.Mode.HorizontalFace, Choose("WorkPlaneBased", PlacementRules.HostKind.Horizontal, new PlacementRules.Options { HasRotation = true }, out _));
            Assert.Equal(PlacementRules.Mode.LevelPlane, Choose("WorkPlaneBased", PlacementRules.HostKind.None, new PlacementRules.Options { HasHeight = true }, out _));
            Assert.Equal(PlacementRules.Mode.FaceDownPlane, Choose("WorkPlaneBased", PlacementRules.HostKind.None, new PlacementRules.Options { FaceDown = true, HasHeight = true }, out _));
        }

        [Fact]
        public void LevelBasedFamilyIgnoresAHostWithAWarning()
        {
            var mode = PlacementRules.Choose("OneLevelBased", PlacementRules.HostKind.Wall, new PlacementRules.Options(), out var error, out var warning);
            Assert.Equal(PlacementRules.Mode.Level, mode);
            Assert.Null(error);
            Assert.Contains("ignored", warning);
        }

        [Theory]
        [InlineData("OneLevelBased", PlacementRules.HostKind.None, "offset", "INVALID_PARAMS")]
        [InlineData("OneLevelBased", PlacementRules.HostKind.None, "side", "INVALID_PARAMS")]
        [InlineData("OneLevelBasedHosted", PlacementRules.HostKind.None, "", "HOST_REQUIRED")]
        [InlineData("OneLevelBasedHosted", PlacementRules.HostKind.Wall, "rotation", "INVALID_PARAMS")]
        [InlineData("OneLevelBasedHosted", PlacementRules.HostKind.Horizontal, "side", "HOST_NOT_WALL")]
        [InlineData("WorkPlaneBased", PlacementRules.HostKind.Wall, "", "SIDE_REQUIRED")]
        [InlineData("WorkPlaneBased", PlacementRules.HostKind.Horizontal, "height", "INVALID_PARAMS")]
        [InlineData("WorkPlaneBased", PlacementRules.HostKind.None, "offset", "INVALID_PARAMS")]
        [InlineData("WorkPlaneBased", PlacementRules.HostKind.Other, "", "UNSUPPORTED_HOST")]
        [InlineData("TwoLevelsBased", PlacementRules.HostKind.None, "", "UNSUPPORTED_PLACEMENT")]
        public void PlacementCombinationsAreRefused(string type, PlacementRules.HostKind host, string option, string code)
        {
            var o = new PlacementRules.Options
            {
                HasHostOffset = option == "offset",
                HasSide = option == "side",
                HasRotation = option == "rotation",
                HasHeight = option == "height"
            };
            Assert.Null(Choose(type, host, o, out var error));
            Assert.StartsWith(code + ":", error);
        }

        // ---- FamilyMeasure ----------------------------------------------------------------------------

        [Theory]
        [InlineData(0, 2700.0, 2700.0, 2700, 2700)]     // took the point's z as its offset: by its parameter and location
        [InlineData(0, null, 2700.0, 2700, 2700)]       // only the location tells
        [InlineData(0, 2700.0, null, 2700, 2700)]       // only the parameter tells
        [InlineData(850, 3550.0, 3550.0, 2700, 2700)]   // with an offset asked for
        [InlineData(0, 0.0, 0.0, 2700, 0)]              // stands right
        [InlineData(0, 400.0, 400.0, 2700, 0)]          // another offset is the family's own, not the quirk
        [InlineData(0, 0.0, 0.0, 0, 0)]                 // on a level at 0 nothing can show
        public void BaseOffsetIsOnlyTheLevelsElevationTakenAsOffset(double wanted, double? parameter, double? location, double elevation, double expected)
        {
            Assert.Equal(expected, FamilyMeasure.BaseOffsetMm(wanted, parameter, location, elevation));
        }

        [Fact]
        public void BoxesAreRelativeToThePointAndTheDefaultLevelIsTheLowestAboveZero()
        {
            Assert.Equal(new[] { -952.0, -455.0, 2700.0 }, FamilyMeasure.Relative(P(-952, -455, 5400), P(0, 0, 2700)));
            var levels = new Dictionary<string, double> { ["GL"] = -600, ["Ground Floor"] = 0, ["Floor 2"] = 5400, ["Floor 1"] = 2700 };
            Assert.Equal("Floor 1", FamilyMeasure.DefaultLevel(levels));
            Assert.Equal("GL", FamilyMeasure.DefaultLevel(new Dictionary<string, double> { ["GL"] = -600, ["Ground Floor"] = 0 }));
            Assert.Null(FamilyMeasure.DefaultLevel(new Dictionary<string, double>()));
        }

        // ---- BoxFamily ---------------------------------------------------------------------------------

        [Fact]
        public void ABoxHasItsOriginAtTheMiddleOfItsBackWithItsFrontTowardsMinusY()
        {
            var box = BoxFamily.Box(1500, 2000, 450);
            Assert.Equal(new[] { -750.0, -2000.0, 0.0 }, box[0]);
            Assert.Equal(new[] { 750.0, 0.0, 450.0 }, box[1]);
            Assert.Equal("1500 x 2000 x 450mm (Plinth)", BoxFamily.DefaultTypeName(1500, 2000, 450));
            Assert.Equal("1500 x 1999.5 x 500mm (Plinth)", BoxFamily.DefaultTypeName(1500, 1999.5, 500.04));
        }

        [Theory]
        [InlineData("Plinth Bed", null)]
        [InlineData("Plinth Side table 2", null)]
        [InlineData("", "empty")]
        [InlineData(" Plinth Bed", "space")]
        [InlineData("Plinth: Bed", "':'")]
        [InlineData("Bed [Plinth]", "'['")]
        [InlineData("Bed.", "dot")]
        public void BoxFamilyNamesMustBeFileAndFamilyNames(string name, string error)
        {
            var got = BoxFamily.NameError(name);
            if (error == null) Assert.Null(got);
            else Assert.Contains(error, got);
        }

        [Fact]
        public void BoxSizesAndTemplates()
        {
            Assert.Null(BoxFamily.SizeError(1500, 2000, 450));
            Assert.StartsWith("depth_mm", BoxFamily.SizeError(1500, 0, 450));
            Assert.StartsWith("height_mm", BoxFamily.SizeError(1500, 2000, 25000));
            Assert.StartsWith("width_mm", BoxFamily.SizeError(double.NaN, 2000, 450));
            Assert.Equal(new[] { "Metric Furniture.rft", "Metric Generic Model.rft" }, BoxFamily.Templates("OST_Furniture"));
            Assert.Equal(new[] { "Metric Generic Model.rft" }, BoxFamily.Templates("OST_GenericModel"));
            Assert.Equal(new[] { "Metric Generic Model.rft" }, BoxFamily.Templates("OST_LightingFixtures"));
        }

        [Fact]
        public void BoxConnectorsAreOptionalAndDefaultToAPowerBalancedOnTheBack()
        {
            Assert.Null(BoxFamily.ConnectorsError(null, out var none));
            Assert.Empty(none);
            Assert.Null(BoxFamily.ConnectorsError(JValue.CreateNull(), out none));
            Assert.Empty(none);

            Assert.Null(BoxFamily.ConnectorsError(JArray.Parse(
                @"[{""domain"":""electrical""},
                   {""domain"":""Pipe"",""classification"":""domesticcoldwater"",""diameter_mm"":15,""face"":""Bottom""},
                   {""domain"":""electrical"",""system_type"":""data"",""face"":""top""}]"), out var connectors));
            Assert.Equal(3, connectors.Count);
            Assert.Equal(("electrical", "PowerBalanced", 0.0, "back"), Tuple(connectors[0]));
            Assert.Null(connectors[0].VoltageV);
            Assert.Equal(("pipe", "DomesticColdWater", 15.0, "bottom"), Tuple(connectors[1]));
            Assert.Equal(("electrical", "Data", 0.0, "top"), Tuple(connectors[2]));
        }

        [Theory]
        [InlineData(null, "PowerBalanced")]
        [InlineData("PowerCircuit", "PowerBalanced")]
        [InlineData("powercircuit", "PowerBalanced")]
        [InlineData("PowerBalanced", "PowerBalanced")]
        [InlineData("powerunbalanced", "PowerUnBalanced")]
        [InlineData("FireAlarm", "FireAlarm")]
        public void BoxPowerConnectorsAreMadeBalancedOrUnbalanced(string asked, string made)
        {
            var item = new JObject { ["domain"] = "electrical" };
            if (asked != null) item["system_type"] = asked;
            Assert.Null(BoxFamily.ConnectorsError(new JArray(item), out var connectors));
            Assert.Equal(made, connectors[0].SystemType);
        }

        [Fact]
        public void BoxPowerConnectorsTakeAVoltage()
        {
            Assert.Null(BoxFamily.ConnectorsError(JArray.Parse(
                @"[{""domain"":""electrical"",""voltage_v"":230},
                   {""domain"":""electrical"",""system_type"":""PowerCircuit"",""voltage_v"":110.5},
                   {""domain"":""electrical"",""system_type"":""PowerUnBalanced"",""voltage_v"":1000},
                   {""domain"":""electrical"",""voltage_v"":null}]"), out var connectors));
            Assert.Equal(new double?[] { 230, 110.5, 1000, null }, connectors.Select(c => c.VoltageV).ToArray());
        }

        [Theory]
        [InlineData(@"[{""domain"":""electrical"",""voltage_v"":0}]", "voltage_v must be a number of volts above 0 and at most 1000")]
        [InlineData(@"[{""domain"":""electrical"",""voltage_v"":-230}]", "voltage_v must be")]
        [InlineData(@"[{""domain"":""electrical"",""voltage_v"":1000.5}]", "voltage_v must be")]
        [InlineData(@"[{""domain"":""electrical"",""voltage_v"":""230""}]", "voltage_v must be")]
        [InlineData(@"[{""domain"":""electrical"",""system_type"":""Data"",""voltage_v"":24}]", "voltage_v is only for a power connector")]
        [InlineData(@"[{""domain"":""pipe"",""classification"":""Sanitary"",""diameter_mm"":100,""voltage_v"":230}]", "does not take voltage_v")]
        public void BadBoxConnectorVoltagesAreRefused(string json, string error)
        {
            Assert.Contains(error, BoxFamily.ConnectorsError(JToken.Parse(json), out _));
        }

        [Fact]
        public void ABoxConnectorIsOnItsFaceWhenOnItsPlaneAndInsideItsEdges()
        {
            // 1200 wide, 600 deep, 900 high: x -600..600, y -600..0, z 0..900.
            Assert.Null(BoxFamily.OnFace("back", P(0, 0, 450), 1200, 600, 900, 0.01));
            Assert.Null(BoxFamily.OnFace("back", P(600, 0, 900), 1200, 600, 900, 0.01));      // a corner is on it
            Assert.Null(BoxFamily.OnFace("right", P(600.005, -300, 450), 1200, 600, 900, 0.01));
            Assert.Null(BoxFamily.OnFace("bottom", P(-100, -599, 0), 1200, 600, 900, 0.01));
            Assert.Contains("off the face's plane", BoxFamily.OnFace("front", P(0, -560, 450), 1200, 600, 900, 0.01));
            Assert.Contains("off the face's plane", BoxFamily.OnFace("top", P(0, -300, 850), 1200, 600, 900, 0.01));
            Assert.Contains("along x, outside the face's edges at -600 to 600", BoxFamily.OnFace("back", P(650, 0, 450), 1200, 600, 900, 0.01));
            Assert.Contains("along z, outside", BoxFamily.OnFace("left", P(-600, -300, 950), 1200, 600, 900, 0.01));
            Assert.Contains("along y, outside", BoxFamily.OnFace("bottom", P(0, 10, 0), 1200, 600, 900, 0.01));
        }

        private static (string, string, double, string) Tuple(BoxConnector c) => (c.Domain, c.SystemType, c.DiameterMm, c.Face);

        [Theory]
        [InlineData(@"{""domain"":""electrical""}", "list")]
        [InlineData(@"[1]", "connectors[0] must be an object")]
        [InlineData(@"[{""domain"":""duct""}]", "domain must be electrical or pipe")]
        [InlineData(@"[{}]", "domain must be electrical or pipe")]
        [InlineData(@"[{""domain"":""electrical"",""system_type"":""Lighting""}]", "system_type must be one of")]
        [InlineData(@"[{""domain"":""electrical"",""system_type"":""UndefinedSystemType""}]", "system_type must be one of")]
        [InlineData(@"[{""domain"":""electrical"",""diameter_mm"":20}]", "does not take diameter_mm")]
        [InlineData(@"[{""domain"":""electrical"",""face"":""side""}]", "face must be one of back, front, left, right, top, bottom")]
        [InlineData(@"[{""domain"":""pipe"",""diameter_mm"":15}]", "classification must be one of")]
        [InlineData(@"[{""domain"":""pipe"",""classification"":""Global"",""diameter_mm"":15}]", "classification must be one of")]
        [InlineData(@"[{""domain"":""pipe"",""classification"":""Sanitary""}]", "diameter_mm must be")]
        [InlineData(@"[{""domain"":""pipe"",""classification"":""Sanitary"",""diameter_mm"":0}]", "diameter_mm must be")]
        [InlineData(@"[{""domain"":""pipe"",""classification"":""Sanitary"",""diameter_mm"":""100""}]", "diameter_mm must be")]
        [InlineData(@"[{""domain"":""pipe"",""classification"":""Sanitary"",""diameter_mm"":700}]", "at most 600")]
        [InlineData(@"[{""domain"":""pipe"",""classification"":""Vent"",""diameter_mm"":50,""system_type"":""Vent""}]", "does not take system_type")]
        [InlineData(@"[{""domain"":""electrical""},{""domain"":""pipe"",""classification"":""Vent"",""diameter_mm"":50,""dia"":1}]", "connectors[1] (pipe) does not take dia")]
        public void BadBoxConnectorsAreRefused(string json, string error)
        {
            Assert.Contains(error, BoxFamily.ConnectorsError(JToken.Parse(json), out _));
        }

        [Fact]
        public void AtMostTwelveBoxConnectors()
        {
            var many = new JArray(Enumerable.Range(0, 13).Select(_ => new JObject { ["domain"] = "electrical" }));
            Assert.Contains("at most 12", BoxFamily.ConnectorsError(many, out _));
            many.RemoveAt(0);
            Assert.Null(BoxFamily.ConnectorsError(many, out var twelve));
            Assert.Equal(12, twelve.Count);
        }

        [Fact]
        public void BoxConnectorsSitAtTheirFaceCentresAndSpreadWhenTheyShareOne()
        {
            BoxConnector On(string face) => new BoxConnector { Domain = "electrical", SystemType = "PowerBalanced", Face = face };
            // 1200 wide, 600 deep, 900 high; the origin is the middle of the back edge at the level.
            var single = BoxFamily.ConnectorPositions(1200, 600, 900,
                new[] { On("back"), On("front"), On("left"), On("right"), On("top"), On("bottom") });
            Assert.Equal(new[] { 0.0, 0.0, 450.0 }, single[0]);
            Assert.Equal(new[] { 0.0, -600.0, 450.0 }, single[1]);
            Assert.Equal(new[] { -600.0, -300.0, 450.0 }, single[2]);
            Assert.Equal(new[] { 600.0, -300.0, 450.0 }, single[3]);
            Assert.Equal(new[] { 0.0, -300.0, 900.0 }, single[4]);
            Assert.Equal(new[] { 0.0, -300.0, 0.0 }, single[5]);

            // Two on the back split its width in thirds; the left one between them stays centred; three on the right
            // split the depth in quarters.
            var shared = BoxFamily.ConnectorPositions(1200, 600, 900,
                new[] { On("back"), On("left"), On("back"), On("right"), On("right"), On("right") });
            Assert.Equal(-200.0, shared[0][0], 6);
            Assert.Equal(new[] { -600.0, -300.0, 450.0 }, shared[1]);
            Assert.Equal(200.0, shared[2][0], 6);
            Assert.Equal(new[] { -450.0, -300.0, -150.0 }, shared.Skip(3).Select(p => p[1]).ToArray());
            Assert.All(shared.Skip(3), p => Assert.Equal(600.0, p[0]));

            // Every position lies on its face's plane.
            var faces = new[] { "back", "left", "back", "right", "right", "right" };
            for (var i = 0; i < faces.Length; i++)
            {
                var n = BoxFamily.FaceNormal(faces[i]);
                var axis = Array.FindIndex(n, v => v != 0);
                Assert.Equal(BoxFamily.FaceCoordinate(faces[i], 1200, 600, 900), shared[i][axis], 6);
            }
            Assert.Equal(-600.0, BoxFamily.FaceCoordinate("front", 1200, 600, 900));
            Assert.Equal(new[] { 0.0, 0.0, -1.0 }, BoxFamily.FaceNormal("bottom"));
        }

        [Theory]
        [InlineData(850, 0.0, 850)]       // the hob live on 2026-11-17: asked 850 above the level, left on it
        [InlineData(850, 849.5, 0)]       // raised: within 1 mm
        [InlineData(850, 1700.0, -850)]   // too high: the difference, negative
        [InlineData(850, null, 850)]      // no location: nothing is known to be raised
        [InlineData(0.4, 0.0, 0)]         // on the level is on the level
        public void HeightStillToGoIsWhatARaiseHasLeft(double wanted, double? actual, double expected)
        {
            Assert.Equal(expected, FamilyMeasure.HeightStillToGoMm(wanted, actual), 6);
        }

        [Theory]
        [InlineData("OneLevelBased", true)]
        [InlineData("WorkPlaneBased", true)]
        [InlineData("OneLevelBasedHosted", false)]
        [InlineData("CurveBased", false)]
        public void LevelAndWorkPlaneBasedFamiliesAreMeasured(string placement, bool measured)
        {
            Assert.Equal(measured, FamilyMeasure.NotMeasuredReason(placement) == null);
        }
    }
}
