using System.Collections.Generic;
using System.Linq;
using B2M.Revit.Core.Families;
using Xunit;

namespace B2M.Revit.Core.Tests
{
    public class TypeMatchRuleTests
    {
        private static TypeFacts T(string name, double w, double h, string mark = "", string function = null) =>
            new TypeFacts { Name = name, SizesMm = new double?[] { w, h }, Mark = mark, Function = function };

        private static TypeRequest Req(double w, double h, string mark = null, string name = null) => new TypeRequest
        {
            ExplicitName = name,
            DefaultName = TypeMatchRule.DefaultName(w, h),
            SizesMm = new[] { w, h },
            Labels = new[] { "width", "height" },
            Mark = mark
        };

        [Fact]
        public void DefaultNameIsWidthByHeightInMm()
        {
            Assert.Equal("900 x 2100mm", TypeMatchRule.DefaultName(900, 2100));
            Assert.Equal("900.5 x 2100mm", TypeMatchRule.DefaultName(900.5, 2100));
        }

        [Fact]
        public void MarksCompareTrimmedIgnoringCase()
        {
            Assert.True(TypeMatchRule.SameMark("w1", " W1 "));
            Assert.True(TypeMatchRule.SameMark(null, ""));
            Assert.False(TypeMatchRule.SameMark("D", "D1"));
        }

        [Fact]
        public void WithoutTypeNameATypeOfThisSizeIsReusedWhateverItsName()
        {
            var types = new List<TypeFacts> { T("0915 x 2134mm", 915, 2134), T("Door 900", 900, 2100.3) };
            var d = TypeMatchRule.Decide(types, "M_Single-Flush", Req(900, 2100));
            Assert.Equal(TypeStep.UseExisting, d.Step);
            Assert.Equal("Door 900", d.TypeName);
            Assert.Equal(1, d.Index);
            Assert.Equal("size", d.MatchedBy);
        }

        [Fact]
        public void WithoutMarkTheDefaultNamedThenUnmarkedTypeIsPreferred()
        {
            var types = new List<TypeFacts> { T("A", 900, 2100, "D"), T("B", 900, 2100), T("900 x 2100mm", 900, 2100, "X") };
            Assert.Equal("900 x 2100mm", TypeMatchRule.Decide(types, "F", Req(900, 2100)).TypeName);
            types.RemoveAt(2);
            Assert.Equal("B", TypeMatchRule.Decide(types, "F", Req(900, 2100)).TypeName);
        }

        [Fact]
        public void ATypeWithThisSizeAndMarkIsReused()
        {
            var types = new List<TypeFacts> { T("900 x 2100mm", 900, 2100, "D1"), T("Flush 900", 900, 2100, "d") };
            var d = TypeMatchRule.Decide(types, "F", Req(900, 2100, "D"));
            Assert.Equal(TypeStep.UseExisting, d.Step);
            Assert.Equal("Flush 900", d.TypeName);
            Assert.Equal("size_and_mark", d.MatchedBy);
        }

        [Fact]
        public void SizeMatchWithAnotherMarkIsLeftAloneAndANewSuffixedTypeCarriesTheMark()
        {
            var types = new List<TypeFacts> { T("900 x 2100mm", 900, 2100, "D1") };
            var d = TypeMatchRule.Decide(types, "F", Req(900, 2100, "D"));
            Assert.Equal(TypeStep.CreateNew, d.Step);
            Assert.Equal("900 x 2100mm D", d.TypeName);

            // An unmarked type of this size is not given the mark either (that would be an edit).
            d = TypeMatchRule.Decide(new List<TypeFacts> { T("900 x 2100mm", 900, 2100) }, "F", Req(900, 2100, "D"));
            Assert.Equal(TypeStep.CreateNew, d.Step);
            Assert.Equal("900 x 2100mm D", d.TypeName);
        }

        [Fact]
        public void NoSizeMatchMakesTheDefaultName()
        {
            var d = TypeMatchRule.Decide(new List<TypeFacts> { T("0915 x 2134mm", 915, 2134) }, "F", Req(800, 2100, "D1"));
            Assert.Equal(TypeStep.CreateNew, d.Step);
            Assert.Equal("800 x 2100mm", d.TypeName);
            Assert.Equal(TypeStep.CreateNew, TypeMatchRule.Decide(new List<TypeFacts>(), "F", Req(800, 2100)).Step);
        }

        [Fact]
        public void DefaultNameTakenByAnotherSizeIsRefused()
        {
            var types = new List<TypeFacts> { T("900 x 2100mm", 915, 2134) };
            var d = TypeMatchRule.Decide(types, "F", Req(900, 2100));
            Assert.Equal(TypeStep.Refuse, d.Step);
            Assert.Equal("TYPE_DIMENSION_MISMATCH", d.Code);
            Assert.Equal(0, d.Index);
            Assert.Contains("pass type_name", d.Error);
            Assert.Equal(915, d.Data["actual_mm"].Value<double>("width"));
        }

        [Fact]
        public void BothDefaultNamesTakenIsRefusedOnTheSuffixedOne()
        {
            var types = new List<TypeFacts> { T("900 x 2100mm", 915, 2134), T("900 x 2100mm D", 900, 2100, "X") };
            var d = TypeMatchRule.Decide(types, "F", Req(900, 2100, "D"));
            Assert.Equal("TYPE_MARK_MISMATCH", d.Code);
            Assert.Equal("900 x 2100mm D", d.TypeName);
            Assert.Equal(1, d.Index);
            Assert.Equal("X", d.Data.Value<string>("actual_type_mark"));
            Assert.Equal("D", d.Data.Value<string>("requested_type_mark"));
        }

        [Fact]
        public void AMarkThatCannotBeInATypeNameIsRefused()
        {
            var d = TypeMatchRule.Decide(new List<TypeFacts> { T("900 x 2100mm", 900, 2100, "A") }, "F", Req(900, 2100, "D:1"));
            Assert.Equal(TypeStep.Refuse, d.Step);
            Assert.Equal("INVALID_PARAMS", d.Code);
        }

        [Fact]
        public void ExplicitNameIsMatchedByNameAndNeverEdited()
        {
            var types = new List<TypeFacts> { T("Entrance", 1000, 2100, "E") };
            var d = TypeMatchRule.Decide(types, "F", Req(1000, 2100, null, "entrance"));
            Assert.Equal(TypeStep.UseExisting, d.Step);
            Assert.Equal("name", d.MatchedBy);

            d = TypeMatchRule.Decide(types, "F", Req(1000, 2100, "E", "Entrance"));
            Assert.Equal(TypeStep.UseExisting, d.Step);

            d = TypeMatchRule.Decide(types, "F", Req(1000, 2100, "D", "Entrance"));
            Assert.Equal("TYPE_MARK_MISMATCH", d.Code);
            Assert.StartsWith("TYPE_MARK_MISMATCH: type 'Entrance' of family 'F' exists with Type Mark 'E', not 'D'. Existing types are never edited", d.Error);

            d = TypeMatchRule.Decide(types, "F", Req(900, 2100, "E", "Entrance"));
            Assert.Equal("TYPE_DIMENSION_MISMATCH", d.Code);

            d = TypeMatchRule.Decide(types, "F", Req(900, 2100, "E", "Main door"));
            Assert.Equal(TypeStep.CreateNew, d.Step);
            Assert.Equal("Main door", d.TypeName);
        }

        [Fact]
        public void ExistingTypeWithoutMarkIsAMarkMismatch()
        {
            var d = TypeMatchRule.Decide(new List<TypeFacts> { T("W", 1500, 1200) }, "F", Req(1500, 1200, "W", "W"));
            Assert.Equal("TYPE_MARK_MISMATCH", d.Code);
            Assert.Contains("exists with no Type Mark", d.Error);
        }

        [Fact]
        public void UnreadableSizesNeverMatch()
        {
            var types = new List<TypeFacts> { new TypeFacts { Name = "X", SizesMm = new double?[] { null, 2100 } } };
            Assert.Equal(TypeStep.CreateNew, TypeMatchRule.Decide(types, "F", Req(900, 2100)).Step);
        }

        [Fact]
        public void ClosestTypeIsCopied()
        {
            var types = new List<TypeFacts> { T("A", 1800, 2100), T("B", 915, 2134), T("C", 762, 2032) };
            Assert.Equal(1, TypeMatchRule.ClosestIndex(types, new double[] { 900, 2100 }));
            Assert.Equal(-1, TypeMatchRule.ClosestIndex(new List<TypeFacts>(), new double[] { 900, 2100 }));
        }
    }

    public class FamilyChoiceRuleTests
    {
        private static TypeFacts T(string name, double w, double h, string mark = "", string function = null) =>
            new TypeFacts { Name = name, SizesMm = new double?[] { w, h }, Mark = mark, Function = function };

        private static FamilyFacts F(string name, params TypeFacts[] types) => new FamilyFacts { Name = name, Types = types.ToList() };

        private static TypeRequest Req(double w, double h, string mark = null) => new TypeRequest
        {
            DefaultName = TypeMatchRule.DefaultName(w, h),
            SizesMm = new[] { w, h },
            Labels = new[] { "width", "height" },
            Mark = mark
        };

        private static List<FamilyFacts> LibraryDoors() => new List<FamilyFacts>
        {
            F("M_Single-Flush", T("0915 x 2134mm", 915, 2134), T("0762 x 2032mm", 762, 2032)),
            F("M_Double-Flush", T("1830 x 2134mm", 1830, 2134)),
            F("M_Door-Exterior-Single-Entry-Half Flat Glass", T("0915 x 2134mm", 915, 2134))
        };

        [Fact]
        public void InteriorDoorPicksSingleFlush()
        {
            var c = FamilyChoiceRule.Choose(OpeningKind.Door, Req(900, 2100, "D"), null, LibraryDoors());
            Assert.Null(c.Error);
            Assert.Equal("M_Single-Flush", c.Chosen.Family.Name);
            Assert.Equal(5, c.Chosen.Score);
            Assert.Contains("single leaf", c.Reason);
            Assert.Contains("next best", c.Reason);
            var alternatives = c.Alternatives();
            Assert.Equal(2, alternatives.Count);
            Assert.DoesNotContain(alternatives, a => a.Value<string>("family_name") == "M_Single-Flush");
            Assert.Equal("M_Door-Exterior-Single-Entry-Half Flat Glass", alternatives[0].Value<string>("family_name"));
        }

        [Fact]
        public void WideDoorPicksDoubleLeaf()
        {
            var c = FamilyChoiceRule.Choose(OpeningKind.Door, Req(1500, 2100), null, LibraryDoors());
            Assert.Equal("M_Double-Flush", c.Chosen.Family.Name);
            var single = c.Ranked.First(f => f.Family.Name == "M_Single-Flush");
            Assert.Contains(single.Reasons, r => r.StartsWith("-5"));
        }

        [Fact]
        public void ExteriorOnlyWithTheHint()
        {
            Assert.Equal("M_Single-Flush", FamilyChoiceRule.Choose(OpeningKind.Door, Req(1000, 2100), null, LibraryDoors()).Chosen.Family.Name);
            var c = FamilyChoiceRule.Choose(OpeningKind.Door, Req(1000, 2100), "exterior", LibraryDoors());
            Assert.Equal("M_Door-Exterior-Single-Entry-Half Flat Glass", c.Chosen.Family.Name);
        }

        [Fact]
        public void TypeFunctionCountsAsKind()
        {
            var families = new List<FamilyFacts>
            {
                F("Door A", T("900", 900, 2100, "", "interior")),
                F("Door B", T("900", 900, 2100, "", "exterior"))
            };
            Assert.Equal("Door A", FamilyChoiceRule.Choose(OpeningKind.Door, Req(900, 2100), null, families).Chosen.Family.Name);
            Assert.Equal("Door B", FamilyChoiceRule.Choose(OpeningKind.Door, Req(900, 2100), "entrance", families).Chosen.Family.Name);
        }

        [Fact]
        public void EqualFamiliesAreNeverPickedSilently()
        {
            var families = new List<FamilyFacts> { F("M_Single-Flush", T("a", 915, 2134)), F("M_Single-Flush Vision", T("b", 915, 2134)) };
            var c = FamilyChoiceRule.Choose(OpeningKind.Door, Req(900, 2100), null, families);
            Assert.Null(c.Chosen);
            Assert.Equal("FAMILY_CHOICE_NEEDED", c.Code);
            Assert.Contains("'M_Single-Flush', 'M_Single-Flush Vision'", c.Error);
            Assert.Equal(2, c.Ranked.Count);
        }

        [Fact]
        public void AnExistingTypeOfThisSizeAndMarkBreaksTheTie()
        {
            var families = new List<FamilyFacts>
            {
                F("M_Single-Flush", T("a", 915, 2134)),
                F("M_Single-Flush Vision", T("900 x 2100mm", 900, 2100, "D"))
            };
            var c = FamilyChoiceRule.Choose(OpeningKind.Door, Req(900, 2100, "D"), null, families);
            Assert.Equal("M_Single-Flush Vision", c.Chosen.Family.Name);
            Assert.Equal(9, c.Chosen.Score);
            Assert.Equal(TypeStep.UseExisting, c.Chosen.Type.Step);
        }

        [Fact]
        public void HintWordsWin()
        {
            var families = new List<FamilyFacts> { F("M_Single-Flush", T("a", 915, 2134)), F("PVC Door", T("b", 800, 2000)) };
            var c = FamilyChoiceRule.Choose(OpeningKind.Door, Req(800, 2100, "D1"), "PVC", families);
            Assert.Equal("PVC Door", c.Chosen.Family.Name);
            Assert.Contains("matches hint 'pvc'", c.Reason);
        }

        [Fact]
        public void SpecialDoorsArePenalisedUnlessHinted()
        {
            var families = new List<FamilyFacts> { F("Sliding Door", T("a", 900, 2100)), F("Hinged Door", T("b", 900, 2100)) };
            Assert.Equal("Hinged Door", FamilyChoiceRule.Choose(OpeningKind.Door, Req(900, 2100), null, families).Chosen.Family.Name);
            Assert.Equal("Sliding Door", FamilyChoiceRule.Choose(OpeningKind.Door, Req(900, 2100), "sliding", families).Chosen.Family.Name);
        }

        [Fact]
        public void APoorBestIsAQuestion()
        {
            var c = FamilyChoiceRule.Choose(OpeningKind.Door, Req(900, 2100), null, new List<FamilyFacts> { F("M_Double-Glass", T("a", 1800, 2100)) });
            Assert.Equal("FAMILY_CHOICE_NEEDED", c.Code);
            Assert.Contains("no loaded door family looks right", c.Error);
        }

        [Fact]
        public void TheOnlyNeutralFamilyIsChosen()
        {
            var c = FamilyChoiceRule.Choose(OpeningKind.Door, Req(900, 2100), null, new List<FamilyFacts> { F("Door 1", T("a", 800, 2000)) });
            Assert.Equal("Door 1", c.Chosen.Family.Name);
            Assert.Contains("only loaded door family", c.Reason);
            Assert.Empty(c.Alternatives());
        }

        [Fact]
        public void FamiliesThatCannotCarryTheTypeAreRejectedWithTheReason()
        {
            var families = new List<FamilyFacts>
            {
                new FamilyFacts { Name = "Instance Width Door", ReadProblem = "PARAMETER_NOT_FOUND: no width type parameter" },
                new FamilyFacts { Name = "Formula Door", Types = new List<TypeFacts> { T("a", 800, 2000) }, WriteProblem = "SIZE_PARAMETER_READ_ONLY: x" }
            };
            var c = FamilyChoiceRule.Choose(OpeningKind.Door, Req(900, 2100), null, families);
            Assert.Equal("FAMILY_NOT_FOUND", c.Code);
            Assert.All(c.Ranked, f => Assert.False(f.Eligible));
            Assert.Contains("SIZE_PARAMETER_READ_ONLY", c.Ranked.Single(f => f.Family.Name == "Formula Door").Rejection);
            Assert.Equal("PARAMETER_NOT_FOUND: no width type parameter",
                c.Ranked.Single(f => f.Family.Name == "Instance Width Door").ToJson().Value<string>("rejected_because"));

            // A family whose sizes cannot be set still serves a type it already has.
            families[1].Types.Add(T("900 x 2100mm", 900, 2100));
            c = FamilyChoiceRule.Choose(OpeningKind.Door, Req(900, 2100), null, families);
            Assert.Equal("Formula Door", c.Chosen.Family.Name);
        }

        [Fact]
        public void NoFamiliesIsNotFound()
        {
            var c = FamilyChoiceRule.Choose(OpeningKind.Window, Req(1500, 1200), null, new List<FamilyFacts>());
            Assert.Equal("FAMILY_NOT_FOUND", c.Code);
            Assert.Contains("none is loaded", c.Error);
        }

        [Fact]
        public void WindowsPreferFixedOrCasementAndTieBetweenThem()
        {
            var families = new List<FamilyFacts>
            {
                F("M_Fixed", T("0915 x 1220mm", 915, 1220)),
                F("M_Casement 3x3 with Trim", T("1200 x 1200mm", 1200, 1200)),
                F("M_Skylight", T("a", 600, 600))
            };
            var c = FamilyChoiceRule.Choose(OpeningKind.Window, Req(1500, 1200, "W"), null, families);
            Assert.Equal("FAMILY_CHOICE_NEEDED", c.Code);
            c = FamilyChoiceRule.Choose(OpeningKind.Window, Req(640, 640, "W2"), "fixed", families);
            Assert.Equal("M_Fixed", c.Chosen.Family.Name);
            Assert.Contains(c.Ranked, f => f.Family.Name == "M_Skylight" && f.Score < 0);
        }

        [Fact]
        public void HintWordsIgnoreGenericWords()
        {
            Assert.Equal(new[] { "exterior", "pvc" }, FamilyChoiceRule.Words("Exterior PVC door").ToArray());
        }
    }
}
