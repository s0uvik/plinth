using System.Linq;
using B2M.Revit.Core.Drawing;
using Xunit;

namespace B2M.Revit.Core.Tests
{
    public class ViewRangeRulesTests
    {
        [Fact]
        public void ADownwardRangeIsValid()
        {
            // Level at 0: top 2300, cut 1200, bottom 0, depth -300 (a foundation plan looking into the ground).
            Assert.Null(ViewRangeRules.Validate(2300, 1200, 0, -300));
        }

        [Fact]
        public void EqualPlanesAreAllowed()
        {
            Assert.Null(ViewRangeRules.Validate(1200, 1200, 0, 0));
        }

        [Fact]
        public void ACutAboveTheTopIsRefusedNamingBothPlanes()
        {
            var reason = ViewRangeRules.Validate(1000, 1200, 0, 0);
            Assert.Contains("cut (1200) is above top (1000)", reason);
        }

        [Fact]
        public void DepthBelowBottomIsCheckedEvenWithoutTheMiddlePlanes()
        {
            Assert.Contains("depth (100) is above bottom (0)", ViewRangeRules.Validate(null, null, 0, 100));
            Assert.Contains("depth", ViewRangeRules.Validate(2300, null, null, 2400));
        }

        [Fact]
        public void UnknownPlanesAreNotCompared()
        {
            Assert.Null(ViewRangeRules.Validate(null, 1200, null, null));
            Assert.Null(ViewRangeRules.Validate(null, null, null, null));
        }

        [Fact]
        public void NonFiniteHeightsAreRefused()
        {
            Assert.Contains("top is not a finite height", ViewRangeRules.Validate(double.NaN, 1200, 0, 0));
        }
    }

    public class ReferenceCheckTests
    {
        private static PlannedReference R(int i, string stable, double? pos) => new PlannedReference(i, stable, pos, "r" + i);

        [Fact]
        public void DistinctReferencesAtDistinctPositionsPass()
        {
            Assert.Empty(ReferenceChecks.Conflicts(new[] { R(0, "a", 0), R(1, "b", 3500), R(2, "c", null) }, 0.5));
        }

        [Fact]
        public void TheSameReferenceTwiceIsAConflict()
        {
            var c = Assert.Single(ReferenceChecks.Conflicts(new[] { R(0, "a", null), R(1, "b", 100), R(2, "a", null) }, 0.5));
            Assert.Equal(0, c.First);
            Assert.Equal(2, c.Second);
            Assert.Contains("same reference", c.Reason);
        }

        [Fact]
        public void TwoReferencesAtOnePositionAreAZeroLengthSegment()
        {
            // A wall exterior face and a slab edge flush with it.
            var c = Assert.Single(ReferenceChecks.Conflicts(new[] { R(0, "wall", 0.2), R(1, "slab", 0), R(2, "grid", 4000) }, 0.5));
            Assert.Equal(1, c.Second);
            Assert.Contains("zero-length", c.Reason);
        }

        [Fact]
        public void UnknownPositionsAndMissingStablesAreNotCompared()
        {
            Assert.Empty(ReferenceChecks.Conflicts(new[] { R(0, null, null), R(1, null, null), R(2, "", 5) }, 0.5));
        }
    }

    public class ReferenceNameTests
    {
        [Theory]
        [InlineData("left", "Left")]
        [InlineData("CENTERLEFTRIGHT", "CenterLeftRight")]
        [InlineData("center_front_back", "CenterFrontBack")]
        [InlineData("Centre Left/Right", "CenterLeftRight")]
        [InlineData("bottom", "Bottom")]
        public void InstanceReferenceNamesNormalise(string given, string expected)
        {
            Assert.True(InstanceReferenceNames.TryNormalize(given, out var name));
            Assert.Equal(expected, name);
        }

        [Fact]
        public void UnknownInstanceReferenceNamesAreRefused()
        {
            Assert.False(InstanceReferenceNames.TryNormalize("StrongReference", out _));
            Assert.False(InstanceReferenceNames.TryNormalize(null, out _));
        }

        [Fact]
        public void WallLeftAndRightFollowTheExteriorSide()
        {
            // Exterior to the left of start->end.
            Assert.Equal("exterior", WallSides.Resolve("left", 1));
            Assert.Equal("interior", WallSides.Resolve("right", 1));
            // Flipped wall: exterior to the right.
            Assert.Equal("interior", WallSides.Resolve("Left", -1));
            Assert.Equal("exterior", WallSides.Resolve("right", -1));
            Assert.Equal("exterior", WallSides.Resolve("exterior", 0));
            Assert.Null(WallSides.Resolve("left", 0.1));
            Assert.Null(WallSides.Resolve("top", 1));
        }
    }

    public class FacePickTests
    {
        private static FaceCandidate F(double pos, double dot, int key) => new FaceCandidate(pos, dot, key);

        [Fact]
        public void PositiveEndTakesFacesAlongTheDirectionOutermostFirst()
        {
            // A stepped footing: +X faces at 600 and 900, -X faces at -600 and -900, a top face (dot 0).
            var faces = new[] { F(600, 1, 0), F(-900, -1, 1), F(900, 1, 2), F(-600, -1, 3), F(0, 0, 4) };
            Assert.Equal(new[] { 2, 0 }, FacePick.Outermost(faces, true, 0.5).Select(f => f.Key));
            Assert.Equal(new[] { 1, 3 }, FacePick.Outermost(faces, false, 0.5).Select(f => f.Key));
        }

        [Fact]
        public void FacesOnOnePlaneAppearOnce()
        {
            // A slab edge split into two faces by a join: same plane, the first given (the larger) is kept.
            var faces = new[] { F(3000, 1, 7), F(3000.2, 1, 8), F(2800, 1, 9) };
            Assert.Equal(new[] { 7, 9 }, FacePick.Outermost(faces, true, 0.5).Select(f => f.Key));
        }

        [Fact]
        public void SlightlyTiltedFacesAreNotTaken()
        {
            Assert.Empty(FacePick.Outermost(new[] { F(100, 0.99, 0) }, true, 0.5));
        }
    }

    public class SectionChainTests
    {
        [Fact]
        public void ChainsGoNearestTheBuildingFirst()
        {
            Assert.Equal(new[] { "openings", "beams", "slabs", "foundation", "levels" },
                SectionChains.Ordered(new[] { "levels", "foundation", "slabs", "beams", "openings" }));
        }

        [Fact]
        public void TheExistingSlabsThenLevelsOrderIsKept()
        {
            Assert.Equal(new[] { "slabs", "levels" }, SectionChains.Ordered(new[] { "levels", "slabs" }));
            Assert.Equal(new[] { "levels" }, SectionChains.Ordered(new[] { "LEVELS", "unknown" }));
        }

        [Fact]
        public void AnElementIsCutWhenItStraddlesOrTouchesThePlane()
        {
            Assert.True(SectionChains.IsCut(new[] { -150.0, 150 }, 1));
            Assert.True(SectionChains.IsCut(new[] { 0.5, 300 }, 1));
            Assert.False(SectionChains.IsCut(new[] { 5.0, 300 }, 1));
            Assert.False(SectionChains.IsCut(new[] { -300.0, -2 }, 1));
            Assert.False(SectionChains.IsCut(new double[0], 1));
        }

        [Fact]
        public void AnOpeningIsMeasuredFromTheLevelBelowIt()
        {
            var levels = new[] { 2700.0, 0, 5400 };
            Assert.Equal(1, SectionChains.LevelBelow(levels, 900, 1));
            Assert.Equal(0, SectionChains.LevelBelow(levels, 2699.5, 1));
            Assert.Equal(1, SectionChains.LevelBelow(levels, -450, 1)); // below every level: the lowest
            Assert.Equal(-1, SectionChains.LevelBelow(new double[0], 0, 1));
        }
    }
}
