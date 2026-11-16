using System.Linq;
using B2M.Revit.Core.Drawing;
using Xunit;

namespace B2M.Revit.Core.Tests
{
    public class DimensionChainTests
    {
        private static ChainStop S(double pos, int priority = 0, string source = null, int key = 0) =>
            new ChainStop(pos, priority, source ?? "s" + pos, key);

        [Fact]
        public void StopsAreSortedAndSegmentsFollow()
        {
            var chain = ChainBuilder.Build(new[] { S(17000), S(0), S(3500), S(7500) }, 1);
            Assert.Null(chain.SkipReason);
            Assert.Equal(new[] { 0.0, 3500, 7500, 17000 }, chain.Positions);
            Assert.Equal(new[] { 3500.0, 4000, 9500 }, chain.Segments);
        }

        [Fact]
        public void CoincidingStopsMergeAndTheBetterPriorityIsKept()
        {
            // An opening edge (priority 2) at a wall corner (priority 0) would make a zero-length segment.
            var chain = ChainBuilder.Build(new[] { S(0.4, 2, "door left", 7), S(0, 0, "wall face", 3), S(900, 2, "door right", 8) }, 1);
            Assert.Equal(new[] { 0.0, 900 }, chain.Positions);
            Assert.Equal(3, chain.Stops[0].Key);
            var merged = Assert.Single(chain.Merged);
            Assert.Equal("door left", merged.Dropped.Source);
            Assert.Equal("wall face", merged.KeptInstead.Source);
        }

        [Fact]
        public void EqualPriorityKeepsTheFirstGiven()
        {
            var chain = ChainBuilder.Build(new[] { S(100, 1, "a", 1), S(100, 1, "b", 2), S(500, 1, "c", 3) }, 1);
            Assert.Equal(1, chain.Stops[0].Key);
        }

        [Fact]
        public void ClustersAreMeasuredFromTheirFirstStopSoTheyCannotCreep()
        {
            // 0, 0.8, 1.6, 2.4 with tolerance 1: 0 and 0.8 merge; 1.6 starts a new cluster with 2.4.
            var chain = ChainBuilder.Build(new[] { S(0), S(0.8), S(1.6), S(2.4) }, 1);
            Assert.Equal(new[] { 0.0, 1.6 }, chain.Positions);
            Assert.Equal(2, chain.Merged.Count);
        }

        [Fact]
        public void ChainsWithoutTwoDistinctStopsAreSkipped()
        {
            Assert.Equal("no references found", ChainBuilder.Build(new ChainStop[0], 1).SkipReason);
            Assert.Equal("only one distinct reference position", ChainBuilder.Build(new[] { S(10), S(10.5) }, 1).SkipReason);
            Assert.Null(ChainBuilder.Build(new[] { S(10), S(12) }, 1).SkipReason);
        }

        [Fact]
        public void NonFiniteStopsAreIgnored()
        {
            var chain = ChainBuilder.Build(new[] { S(double.NaN), S(0), S(double.PositiveInfinity), S(2700) }, 1);
            Assert.Equal(new[] { 0.0, 2700 }, chain.Positions);
        }

        [Fact]
        public void SameChainComparesPositionsWithinTolerance()
        {
            Assert.True(ChainBuilder.SameChain(new[] { 0.0, 17000 }, new[] { 0.5, 17000.2 }, 1));
            Assert.False(ChainBuilder.SameChain(new[] { 0.0, 17000 }, new[] { 0.0, 4250, 17000 }, 1));
            Assert.False(ChainBuilder.SameChain(new[] { 0.0, 17000 }, new[] { 0.0, 16990 }, 1));
        }

        [Fact]
        public void SectionStoreysReadAsSlabAndClearHeights()
        {
            // Levels every 2700 with a 150 slab under each upper level: the slab top merges into the level.
            var levels = new[] { 0.0, 2700, 5400 }.Select((z, i) => S(z, 0, "level " + i, i));
            var slabs = new[] { 2700.0, 2550, 5400, 5250 }.Select((z, i) => S(z, 5, "floor " + i, 10 + i));
            var chain = ChainBuilder.Build(levels.Concat(slabs), 1);
            Assert.Equal(new[] { 2550.0, 150, 2550, 150 }, chain.Segments);
            Assert.All(chain.Merged, m => Assert.StartsWith("level", m.KeptInstead.Source));
        }
    }

    public class SkylineTests
    {
        [Fact]
        public void ARectangleShowsOnlyItsOuterFace()
        {
            // Top wall exterior face at y=10000 over 0..17000, an interior face at y=6000 under it.
            var pieces = Skyline.Visible(new[] { new FaceSpan(10000, 0, 17000, 1), new FaceSpan(6000, 250, 8000, 2) }, 1, 10);
            var piece = Assert.Single(pieces);
            Assert.Equal(1, piece.Face.Key);
            Assert.Equal(0, piece.Min);
            Assert.Equal(17000, piece.Max);
        }

        [Fact]
        public void ARecessShowsTheSetBackFaceBetweenTheProjections()
        {
            var faces = new[]
            {
                new FaceSpan(10000, 0, 5000, 1),
                new FaceSpan(10000, 12000, 17000, 2),
                new FaceSpan(8500, 4750, 12250, 3) // recessed wall, overlapped by the projections' side walls
            };
            var pieces = Skyline.Visible(faces, 1, 10);
            Assert.Equal(new[] { 1, 3, 2 }, pieces.Select(p => p.Face.Key));
            Assert.Equal(5000, pieces[1].Min);
            Assert.Equal(12000, pieces[1].Max);
        }

        [Fact]
        public void LookingFromBelowUsesTheLowestFaces()
        {
            var pieces = Skyline.Visible(new[] { new FaceSpan(0, 0, 17000, 1), new FaceSpan(4000, 0, 17000, 2) }, -1, 10);
            Assert.Equal(1, Assert.Single(pieces).Face.Key);
        }

        [Fact]
        public void SlivesShorterThanTheMinimumAreIgnored()
        {
            var pieces = Skyline.Visible(new[] { new FaceSpan(100, 0, 1000, 1), new FaceSpan(50, 995, 1500, 2) }, 1, 10);
            Assert.Equal(2, pieces.Count);
            Assert.Equal(1000, pieces[1].Min);
            pieces = Skyline.Visible(new[] { new FaceSpan(100, 0, 1000, 1), new FaceSpan(50, 0, 1005, 2) }, 1, 10);
            Assert.Single(pieces);
        }
    }

    public class RaycastTests
    {
        [Fact]
        public void FindsTheNearestFaceCoveringThePoint()
        {
            var faces = new[]
            {
                new FaceSpan(250, 0, 10000, 1),   // inner face of the left exterior wall
                new FaceSpan(3750, 0, 4000, 2),   // a partition face that does not reach y=5000
                new FaceSpan(3875, 4000, 10000, 3)
            };
            var hit = Raycast.Nearest(faces, 2000, 5000, 1, 1);
            Assert.Equal(3, hit.Key);
            Assert.Equal(1, Raycast.Nearest(faces, 2000, 5000, -1, 1).Key);
            Assert.Null(Raycast.Nearest(faces, 100, 5000, -1, 1));
        }
    }

    public class SideMathTests
    {
        [Theory]
        [InlineData("top", 0, 1, 1)]
        [InlineData("bottom", 0, 1, -1)]
        [InlineData("left", 1, 0, -1)]
        [InlineData("RIGHT", 1, 0, 1)]
        public void SidesMapToAxesAndDirections(string name, int measure, int normal, int sign)
        {
            Assert.True(SideMath.TryParse(name, out var side));
            Assert.Equal(measure, SideMath.MeasureAxis(side));
            Assert.Equal(normal, SideMath.NormalAxis(side));
            Assert.Equal(sign, SideMath.OutwardSign(side));
        }

        [Fact]
        public void UnknownSideIsRejected()
        {
            Assert.False(SideMath.TryParse("north", out _));
        }

        [Fact]
        public void ChainLinesStepOutwardFromTheBuilding()
        {
            // Building from y=0 to y=13375: top chains at 14175, 14775, 15375; bottom chains at -800, -1400.
            var top = SideMath.OuterExtent(0, 13375, 1);
            Assert.Equal(14175, SideMath.ChainLine(top, 1, 800, 600, 0));
            Assert.Equal(15375, SideMath.ChainLine(top, 1, 800, 600, 2));
            var bottom = SideMath.OuterExtent(0, 13375, -1);
            Assert.Equal(-1400, SideMath.ChainLine(bottom, -1, 800, 600, 1));
        }

        [Theory]
        [InlineData(2700.0, "2700")]
        [InlineData(2687.5, "2687.5")]
        [InlineData(-0.01, "0")]
        public void MillimetresFormatForReasons(double value, string text)
        {
            Assert.Equal(text, SideMath.Mm(value));
        }
    }

    public class RoomLabelTests
    {
        [Theory]
        [InlineData(3498.7, 4001.2, "x_first", "3500 X 4000")]
        [InlineData(4001.2, 3498.7, "x_first", "4000 X 3500")]
        [InlineData(3498.7, 4001.2, "longer_first", "4000 X 3500")]
        [InlineData(4001.2, 3498.7, "shorter_first", "3500 X 4000")]
        [InlineData(4500, 4000, "shorter_first", "4000 X 4500")]
        [InlineData(2687.5, 1500, "x_first", "2690 X 1500")]
        public void SizesRoundToFiveAndOrder(double x, double y, string order, string text)
        {
            Assert.Equal(text, RoomSizeText.Format(x, y, order, 5));
        }

        [Fact]
        public void SeparatorAndStepAreParameters()
        {
            Assert.Equal("3499x4001", RoomSizeText.Format(3498.7, 4001.2, "x_first", 0, "x"));
            Assert.Equal("3500 X 4000", RoomSizeText.Format(3540, 3960, "x_first", 100));
            Assert.Equal(2.5, RoomSizeText.Round(2.4, 0.5));
        }

        [Fact]
        public void UnknownOrderThrows()
        {
            Assert.Throws<System.ArgumentException>(() => RoomSizeText.Format(1, 2, "y_first", 5));
        }

        [Fact]
        public void NoteHangsUnderTheTagByThePaperGapAtScale()
        {
            // 1 mm on paper at 1:100 is 100 mm in the model.
            Assert.Equal(4900, LabelLayout.NoteTopUnderTag(5000, 1, 100));
            Assert.Equal(250, LabelLayout.PaperToModel(2.5, 100));
            // Two 2.5 mm lines at 1.5 spacing, centred on the head: the bottom is 3.75 mm (375 model mm) below it.
            Assert.Equal(5000 - 375, LabelLayout.EstimatedTagBottom(5000, 2, 2.5, 100), 6);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => LabelLayout.PaperToModel(1, 0));
        }

        [Fact]
        public void TextTypeNamesAreSizedAndUnique()
        {
            Assert.Equal("B2M 2.5mm", TextTypeNames.ForSize(2.5));
            Assert.Equal("B2M 3mm", TextTypeNames.ForSize(3));
            Assert.Equal("B2M 2.5mm", TextTypeNames.Unique("B2M 2.5mm", new[] { "2.5mm Arial" }));
            Assert.Equal("B2M 2.5mm (3)", TextTypeNames.Unique("B2M 2.5mm", new[] { "b2m 2.5MM", "B2M 2.5mm (2)" }));
            Assert.True(TextTypeNames.SameSize(2.5, 2.505));
            Assert.False(TextTypeNames.SameSize(2.5, 2.52));
        }
    }
}
