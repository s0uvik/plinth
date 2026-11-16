using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using B2M.Revit.Core.Execution;
using B2M.Revit.Core.Families;
using B2M.Revit.Core.Mep;
using Newtonsoft.Json.Linq;
using Xunit;

namespace B2M.Revit.Core.Tests
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

        // ---- TemplateFiles ----------------------------------------------------------------------------

        [Fact]
        public void TemplatesAreListedPerLanguageFolder()
        {
            var root = Path.Combine(Path.GetTempPath(), "b2m-templates-" + Guid.NewGuid().ToString("N"));
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
            var folder = Path.Combine(Path.GetTempPath(), "b2m-cache-" + Guid.NewGuid().ToString("N"));
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
