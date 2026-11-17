using System.Collections.Generic;
using System.Linq;
using Plinth.Revit.Core.Families;
using Plinth.Revit.Core.Geometry;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Plinth.Revit.Core.Tests
{
    public class HostTypeRuleTests
    {
        private static LayerSpec L(string function, double mm, string material = null, long? id = null) =>
            new LayerSpec { Function = function, ThicknessMm = mm, Material = material, MaterialId = id };

        private static HostTypeFacts Wall(string name, string function, params LayerSpec[] layers) => new HostTypeFacts
        {
            Name = name,
            Function = function,
            Layers = layers.ToList(),
            TotalMm = layers.Sum(l => l.ThicknessMm),
            Structural = layers.Any(l => l.Function == "structure")
        };

        private static HostTypeFacts Floor(string name, bool structural, params LayerSpec[] layers)
        {
            var facts = Wall(name, null, layers);
            facts.Structural = structural;
            return facts;
        }

        private static HostTypeRequest WallReq(double t, string function = "interior", string name = null, List<LayerSpec> layers = null,
            string baseName = null) =>
            new HostTypeRequest { Kind = HostKind.Wall, ThicknessMm = t, Function = function, ExplicitName = name, Layers = layers, BaseTypeName = baseName };

        private static HostTypeRequest FloorReq(double t, bool foundation = false, bool structural = false, string name = null) =>
            new HostTypeRequest { Kind = HostKind.Floor, ThicknessMm = t, Foundation = foundation, Structural = structural, ExplicitName = name };

        private static readonly string[] NoOthers = new string[0];

        // ---- names ----------------------------------------------------------------------------------

        [Fact]
        public void DefaultNamesFollowTheContract()
        {
            Assert.Equal("Plinth 230mm Exterior", WallReq(230, "exterior").DefaultName);
            Assert.Equal("Plinth 115mm Interior", WallReq(115).DefaultName);
            Assert.Equal("Plinth 150mm Slab", FloorReq(150).DefaultName);
            Assert.Equal("Plinth 300mm Slab", FloorReq(300, foundation: true).DefaultName);
            Assert.Equal("Plinth 300mm Foundation Slab", FloorReq(300, foundation: true).AlternateDefaultName);
            Assert.Null(WallReq(230).AlternateDefaultName);
        }

        [Fact]
        public void NamesRefuseRevitsForbiddenCharacters()
        {
            Assert.True(HostTypeRule.TryName(null, "name", out var none, out _));
            Assert.Null(none);
            Assert.True(HostTypeRule.TryName(new JValue("  Brick 230 "), "name", out var name, out _));
            Assert.Equal("Brick 230", name);
            Assert.False(HostTypeRule.TryName(new JValue("A:B"), "name", out _, out var error));
            Assert.StartsWith("INVALID_PARAMS:", error);
            Assert.False(HostTypeRule.TryName(new JValue(" "), "name", out _, out _));
        }

        [Fact]
        public void ThicknessMustBePositiveAndBounded()
        {
            Assert.True(HostTypeRule.TryThickness(new JValue(230), out var mm, out _));
            Assert.Equal(230, mm);
            Assert.False(HostTypeRule.TryThickness(new JValue(0), out _, out _));
            Assert.False(HostTypeRule.TryThickness(new JValue(-5), out _, out _));
            Assert.False(HostTypeRule.TryThickness(null, out _, out _));
            Assert.False(HostTypeRule.TryThickness(new JValue(HostTypeRule.MaxThicknessMm + 1), out _, out _));
        }

        // ---- layers ---------------------------------------------------------------------------------

        [Fact]
        public void LayersAreParsedInOrderAndMustAddUp()
        {
            var token = JArray.Parse(@"[{""function"":""finish1"",""thickness_mm"":12,""material"":""Plaster""},
                {""function"":""Structure"",""thickness_mm"":226},{""function"":""finish2"",""thickness_mm"":12}]");
            Assert.True(HostTypeRule.TryLayers(token, 250, out var layers, out var error), error);
            Assert.Equal(new[] { "finish1", "structure", "finish2" }, layers.Select(l => l.Function));
            Assert.Equal("Plaster", layers[0].Material);
            Assert.Null(layers[1].Material);

            Assert.False(HostTypeRule.TryLayers(token, 230, out _, out error));
            Assert.Contains("add up to 250 mm", error);
        }

        [Fact]
        public void AbsentLayersAreNotAnError()
        {
            Assert.True(HostTypeRule.TryLayers(null, 230, out var layers, out var error));
            Assert.Null(layers);
            Assert.Null(error);
        }

        [Theory]
        [InlineData(@"[]", "1 to")]
        [InlineData(@"[{""function"":""finish1"",""thickness_mm"":230}]", "at least one structure layer")]
        [InlineData(@"[{""function"":""core"",""thickness_mm"":230}]", "function must be one of")]
        [InlineData(@"[{""function"":""structure"",""thickness_mm"":226},{""function"":""membrane"",""thickness_mm"":4}]", "membrane")]
        [InlineData(@"[{""function"":""structure"",""thickness_mm"":230},{""function"":""finish1"",""thickness_mm"":0}]", "only a membrane")]
        [InlineData(@"[{""function"":""structure"",""thickness_mm"":230,""colour"":""red""}]", "unknown field 'colour'")]
        [InlineData(@"[{""function"":""structure"",""thickness_mm"":230,""material"":"" ""}]", "material must be a material name")]
        public void BadLayersAreRefusedWithTheReason(string json, string expected)
        {
            Assert.False(HostTypeRule.TryLayers(JArray.Parse(json), 230, out _, out var error));
            Assert.StartsWith("INVALID_PARAMS:", error);
            Assert.Contains(expected, error);
        }

        [Fact]
        public void AZeroMembraneIsAllowed()
        {
            var token = JArray.Parse(@"[{""function"":""structure"",""thickness_mm"":150},{""function"":""membrane"",""thickness_mm"":0}]");
            Assert.True(HostTypeRule.TryLayers(token, 150, out var layers, out var error), error);
            Assert.Equal(2, layers.Count);
        }

        [Fact]
        public void TheCoreSpansFirstToLastStructureLayer()
        {
            HostTypeRule.ShellCounts(new[] { L("finish1", 12), L("structure", 100), L("insulation", 50), L("structure", 100), L("finish2", 12), L("membrane", 0) },
                out var exterior, out var interior);
            Assert.Equal(1, exterior);
            Assert.Equal(2, interior);
            HostTypeRule.ShellCounts(new[] { L("structure", 230) }, out exterior, out interior);
            Assert.Equal(0, exterior);
            Assert.Equal(0, interior);
        }

        // ---- step 1: by name ------------------------------------------------------------------------

        [Fact]
        public void TheDefaultNamedTypeIsReusedWhenItMatches()
        {
            var types = new List<HostTypeFacts>
            {
                Wall("Generic - 230mm", "exterior", L("structure", 230, "Brick")),
                Wall("Plinth 230mm Exterior", "exterior", L("structure", 230, "Brick"))
            };
            var d = HostTypeRule.Decide(types, NoOthers, WallReq(230, "exterior"));
            Assert.Equal(HostTypeStep.UseExisting, d.Step);
            Assert.Equal(1, d.Index);
            Assert.Equal("name", d.MatchedBy);
        }

        [Fact]
        public void ANamedTypeWithAnotherThicknessIsAMismatchNeverEdited()
        {
            var types = new List<HostTypeFacts> { Wall("Outer", "exterior", L("structure", 250)), Wall("Other 230", "exterior", L("structure", 230)) };
            var d = HostTypeRule.Decide(types, NoOthers, WallReq(230, "exterior", name: "outer"));
            Assert.Equal(HostTypeStep.Refuse, d.Step);
            Assert.Equal("TYPE_DIMENSION_MISMATCH", d.Code);
            Assert.Equal(0, d.Index);
            Assert.Contains("250 mm thick, not 230 mm", d.Error);
            Assert.Contains("never edited", d.Error);
            Assert.Equal("Outer", d.Data.Value<string>("type_name"));
            Assert.NotEmpty((JArray)d.Data["differences"]);
        }

        [Fact]
        public void ANamedTypeWithAnotherFunctionOrStackIsAMismatch()
        {
            var types = new List<HostTypeFacts> { Wall("W", "interior", L("structure", 230)) };
            Assert.Equal("TYPE_DIMENSION_MISMATCH", HostTypeRule.Decide(types, NoOthers, WallReq(230, "exterior", name: "W")).Code);

            var asked = new List<LayerSpec> { L("finish1", 15), L("structure", 200), L("finish2", 15) };
            var d = HostTypeRule.Decide(types, NoOthers, WallReq(230, "interior", name: "W", layers: asked));
            Assert.Equal("TYPE_DIMENSION_MISMATCH", d.Code);
            Assert.Contains("1 layer", d.Error);
        }

        [Fact]
        public void AnExplicitNameHeldByANonCandidateIsNameInUse()
        {
            var types = new List<HostTypeFacts> { Wall("Generic 200", "interior", L("structure", 200)) };
            var d = HostTypeRule.Decide(types, new[] { "Storefront" }, WallReq(115, name: "Storefront"));
            Assert.Equal("NAME_IN_USE", d.Code);
        }

        [Fact]
        public void AFoundationDefaultNameHeldByAFloorTypeMovesToTheAlternate()
        {
            var foundations = new List<HostTypeFacts> { Floor("Foundation Slab 450", true, L("structure", 450, "Concrete")) };
            var d = HostTypeRule.Decide(foundations, new[] { "Plinth 300mm Slab" }, FloorReq(300, foundation: true));
            Assert.Equal(HostTypeStep.CreateNew, d.Step);
            Assert.Equal("Plinth 300mm Foundation Slab", d.TypeName);
        }

        // ---- step 2: by spec ------------------------------------------------------------------------

        [Fact]
        public void AnyTypeWithTheSpecIsReusedPlinthNamesFirst()
        {
            var types = new List<HostTypeFacts>
            {
                Wall("Brick 115", "interior", L("structure", 115, "Brick")),
                Wall("Plinth Partition", "interior", L("structure", 115, "Block")),
                Wall("Exterior 115", "exterior", L("structure", 115))
            };
            var d = HostTypeRule.Decide(types, NoOthers, WallReq(115));
            Assert.Equal(HostTypeStep.UseExisting, d.Step);
            Assert.Equal("Plinth Partition", d.TypeName);
            Assert.Equal("spec", d.MatchedBy);
            Assert.Equal(new[] { "Brick 115" }, d.OtherMatches);
        }

        [Fact]
        public void WithLayersTheWholeStackAndNamedMaterialsMustMatch()
        {
            var types = new List<HostTypeFacts>
            {
                Wall("Plastered brick", "exterior", L("finish1", 12, "Plaster"), L("structure", 226, "Brick"), L("finish2", 12, "Plaster"))
            };
            var same = new List<LayerSpec> { L("finish1", 12), L("structure", 226, "brick"), L("finish2", 12) };
            Assert.Equal(HostTypeStep.UseExisting, HostTypeRule.Decide(types, NoOthers, WallReq(250, "exterior", layers: same)).Step);

            var otherMaterial = new List<LayerSpec> { L("finish1", 12), L("structure", 226, "Concrete"), L("finish2", 12) };
            var d = HostTypeRule.Decide(types, NoOthers, WallReq(250, "exterior", layers: otherMaterial));
            Assert.Equal(HostTypeStep.CreateNew, d.Step);
            Assert.Equal("Plinth 250mm Exterior", d.TypeName);
        }

        [Fact]
        public void ThicknessMatchesWithinHalfAMillimetre()
        {
            var types = new List<HostTypeFacts> { Wall("A", "interior", L("structure", 115.4)) };
            Assert.Equal(HostTypeStep.UseExisting, HostTypeRule.Decide(types, NoOthers, WallReq(115)).Step);
            types[0] = Wall("A", "interior", L("structure", 115.6));
            Assert.Equal(HostTypeStep.CreateNew, HostTypeRule.Decide(types, NoOthers, WallReq(115)).Step);
        }

        [Fact]
        public void AStructuralFloorRequestNeedsAStructuralLayer()
        {
            var types = new List<HostTypeFacts> { Floor("Slab 150", false, L("structure", 150, "Concrete")) };
            Assert.Equal(HostTypeStep.UseExisting, HostTypeRule.Decide(types, NoOthers, FloorReq(150)).Step);
            var d = HostTypeRule.Decide(types, NoOthers, FloorReq(150, structural: true));
            Assert.Equal(HostTypeStep.CreateNew, d.Step);
            Assert.Contains("no " + "floor type has a structural layer", d.BaseReason);
        }

        [Fact]
        public void UnreadableTypesNeverMatchNorServeAsBase()
        {
            var types = new List<HostTypeFacts> { new HostTypeFacts { Name = "Odd", Function = "interior" }, Wall("Generic 100", "interior", L("structure", 100)) };
            var d = HostTypeRule.Decide(types, NoOthers, WallReq(115));
            Assert.Equal(HostTypeStep.CreateNew, d.Step);
            Assert.Equal(1, d.BaseIndex);
        }

        // ---- step 3: the base type ------------------------------------------------------------------

        [Fact]
        public void TheClosestTypeOfTheSameFunctionIsCopiedKeepingItsCoreMaterial()
        {
            var types = new List<HostTypeFacts>
            {
                Wall("Exterior 230", "exterior", L("structure", 230, "Brick", 11)),
                Wall("Interior 100", "interior", L("finish1", 10, "Plaster", 20), L("structure", 80, "Block", 21), L("finish2", 10, "Plaster", 20)),
                Wall("Interior 200", "interior", L("structure", 200, "Concrete", 22))
            };
            var d = HostTypeRule.Decide(types, NoOthers, WallReq(125));
            Assert.Equal(HostTypeStep.CreateNew, d.Step);
            Assert.Equal("Plinth 125mm Interior", d.TypeName);
            Assert.Equal(1, d.BaseIndex);
            Assert.Contains("'Interior 100'", d.BaseReason);
            Assert.Contains("interior Basic Wall types", d.BaseReason);
            var layer = Assert.Single(d.Layers);
            Assert.Equal("structure", layer.Function);
            Assert.Equal(125, layer.ThicknessMm);
            Assert.Equal("Block", layer.Material);
            Assert.Equal(21, layer.MaterialId);
            Assert.True(layer.Inherited);
            Assert.Equal(2, d.Alternatives.Count);
            Assert.Equal("Interior 200", d.Alternatives[0].Value<string>("type_name"));
        }

        [Fact]
        public void WithoutATypeOfTheFunctionAllBasicWallsArePooledAndSaidSo()
        {
            var types = new List<HostTypeFacts> { Wall("Generic 200", "exterior", L("structure", 200, "Brick")) };
            var d = HostTypeRule.Decide(types, NoOthers, WallReq(115));
            Assert.Equal(HostTypeStep.CreateNew, d.Step);
            Assert.Contains("no Basic Wall type has function interior", d.BaseReason);
        }

        [Fact]
        public void ATieThatChangesMaterialsAsksTheUser()
        {
            var types = new List<HostTypeFacts>
            {
                Wall("Brick 100", "interior", L("structure", 100, "Brick")),
                Wall("Block 150", "interior", L("structure", 150, "Block"))
            };
            var d = HostTypeRule.Decide(types, NoOthers, WallReq(125));
            Assert.Equal(HostTypeStep.Refuse, d.Step);
            Assert.Equal("TYPE_CHOICE_NEEDED", d.Code);
            Assert.Contains("base_type_name", d.Error);
            Assert.Equal(2, ((JArray)d.Data["candidates"]).Count);
        }

        [Fact]
        public void ATieWithTheSameMaterialsGoesByName()
        {
            var types = new List<HostTypeFacts>
            {
                Wall("Z Brick 150", "interior", L("structure", 150, "Brick")),
                Wall("A Brick 100", "interior", L("structure", 100, "Brick"))
            };
            var d = HostTypeRule.Decide(types, NoOthers, WallReq(125));
            Assert.Equal(HostTypeStep.CreateNew, d.Step);
            Assert.Equal(1, d.BaseIndex);
            Assert.Contains("tied with 'Z Brick 150'", d.BaseReason);
        }

        [Fact]
        public void ATieDoesNotMatterWhenEveryLayerNamesItsMaterial()
        {
            var types = new List<HostTypeFacts>
            {
                Wall("Brick 100", "interior", L("structure", 100, "Brick")),
                Wall("Block 150", "interior", L("structure", 150, "Block"))
            };
            var layers = new List<LayerSpec> { L("finish1", 12, "Plaster"), L("structure", 101, "Brick"), L("finish2", 12, "Plaster") };
            var d = HostTypeRule.Decide(types, NoOthers, WallReq(125, layers: layers));
            Assert.Equal(HostTypeStep.CreateNew, d.Step);
            Assert.Equal("Block 150", types[d.BaseIndex].Name);
            Assert.All(d.Layers, l => Assert.False(l.Inherited));
            Assert.Null(d.Layers[1].MaterialId);
        }

        [Fact]
        public void GivenLayersWithoutMaterialInheritFromTheBaseByFunction()
        {
            var types = new List<HostTypeFacts>
            {
                Wall("Plastered", "exterior", L("finish1", 15, "Cement Plaster", 5), L("structure", 200, "Brick", 6), L("finish2", 15, "Gypsum", 7))
            };
            var layers = new List<LayerSpec> { L("finish1", 20), L("structure", 190), L("substrate", 20), L("finish2", 20, "Paint") };
            var d = HostTypeRule.Decide(types, NoOthers, WallReq(250, "exterior", layers: layers));
            Assert.Equal(HostTypeStep.CreateNew, d.Step);
            Assert.Equal(new[] { "Cement Plaster", "Brick", null, "Paint" }, d.Layers.Select(l => l.Material));
            Assert.Equal(new long?[] { 5, 6, null, null }, d.Layers.Select(l => l.MaterialId));
            Assert.Equal(new[] { true, true, true, false }, d.Layers.Select(l => l.Inherited));
        }

        [Fact]
        public void AGivenBaseTypeIsUsedEvenWhenAnotherIsCloser()
        {
            var types = new List<HostTypeFacts>
            {
                Wall("Close", "interior", L("structure", 120, "Block")),
                Wall("Far", "interior", L("structure", 300, "Brick"))
            };
            var d = HostTypeRule.Decide(types, NoOthers, WallReq(125, baseName: "far"));
            Assert.Equal(HostTypeStep.CreateNew, d.Step);
            Assert.Equal(1, d.BaseIndex);
            Assert.Equal("base_type_name given by the caller", d.BaseReason);
            Assert.Equal("Brick", d.Layers[0].Material);
        }

        [Fact]
        public void AMissingOrIneligibleBaseTypeIsRefusedWithCandidates()
        {
            var types = new List<HostTypeFacts> { Wall("Generic 200", "interior", L("structure", 200)) };
            var d = HostTypeRule.Decide(types, NoOthers, WallReq(125, baseName: "Nope"));
            Assert.Equal("BASE_TYPE_NOT_FOUND", d.Code);
            Assert.Single((JArray)d.Data["candidates"]);

            d = HostTypeRule.Decide(types, new[] { "Curtain Wall 1" }, WallReq(125, baseName: "Curtain Wall 1"));
            Assert.Equal("BASE_TYPE_NOT_FOUND", d.Code);
            Assert.Contains("curtain or stacked", d.Error);
        }

        [Fact]
        public void NoFoundationSlabTypeToCopyIsRefusedWithAdvice()
        {
            var d = HostTypeRule.Decide(new List<HostTypeFacts>(), new[] { "Generic 300mm" }, FloorReq(300, foundation: true));
            Assert.Equal("BASE_TYPE_NOT_FOUND", d.Code);
            Assert.Contains("foundation slab type can only be copied from another one", d.Error);
        }

        [Fact]
        public void AnExplicitNameNotFoundStillReusesATypeWithTheSpec()
        {
            var types = new List<HostTypeFacts> { Floor("Concrete 150", true, L("structure", 150, "Concrete")) };
            var d = HostTypeRule.Decide(types, NoOthers, FloorReq(150, structural: true, name: "Slab S1"));
            Assert.Equal(HostTypeStep.UseExisting, d.Step);
            Assert.Equal("spec", d.MatchedBy);
            Assert.Equal("Concrete 150", d.TypeName);
        }

        [Fact]
        public void AnExplicitNewNameIsUsedForTheCopy()
        {
            var types = new List<HostTypeFacts> { Floor("Concrete 200", true, L("structure", 200, "Concrete")) };
            var d = HostTypeRule.Decide(types, NoOthers, FloorReq(150, name: "Slab S1"));
            Assert.Equal(HostTypeStep.CreateNew, d.Step);
            Assert.Equal("Slab S1", d.TypeName);
        }
    }

    public class PlanPolygonTests
    {
        private static List<double[]> P(params double[] xy) =>
            Enumerable.Range(0, xy.Length / 2).Select(i => new[] { xy[2 * i], xy[2 * i + 1] }).ToList();

        [Fact]
        public void ARectangleIsValidWithItsArea()
        {
            var rect = P(0, 0, 10000, 0, 10000, 6000, 0, 6000);
            Assert.Null(PlanPolygon.Validate(rect, "points"));
            Assert.Equal(60e6, PlanPolygon.AreaMm2(rect));
            Assert.True(PlanPolygon.SignedAreaMm2(rect) > 0);
            rect.Reverse();
            Assert.Equal(60e6, PlanPolygon.AreaMm2(rect));
        }

        [Fact]
        public void AnLShapeIsValid()
        {
            var l = P(0, 0, 8000, 0, 8000, 3000, 3000, 3000, 3000, 7000, 0, 7000);
            Assert.Null(PlanPolygon.Validate(l, "points"));
            Assert.Equal(8000.0 * 3000 + 3000.0 * 4000, PlanPolygon.AreaMm2(l));
        }

        [Fact]
        public void AFigureOfEightIsRefused()
        {
            var bowtie = P(0, 0, 1000, 1000, 1000, 0, 0, 1000);
            var error = PlanPolygon.Validate(bowtie, "points");
            Assert.StartsWith("INVALID_PARAMS:", error);
            Assert.Contains("cross or touch", error);
        }

        [Fact]
        public void ARepeatedFirstPointIsRefused()
        {
            Assert.NotNull(PlanPolygon.Validate(P(0, 0, 1000, 0, 1000, 1000, 0, 0), "points"));
        }

        [Fact]
        public void PointsOnOneLineAreRefused()
        {
            // All on one line: the outline folds back on itself.
            Assert.StartsWith("INVALID_PARAMS:", PlanPolygon.Validate(P(0, 0, 1000, 0, 2000, 0), "points"));
            // A sliver of half a square millimetre has no real extent.
            Assert.Contains("encloses no area", PlanPolygon.Validate(P(0, 0, 1000, 0, 500, 0.001), "points"));
        }

        [Fact]
        public void ASpikeIsRefused()
        {
            Assert.Contains("fold back", PlanPolygon.Validate(P(0, 0, 2000, 0, 1000, 0, 1000, 1000), "points"));
        }

        [Fact]
        public void TooFewPointsAreRefused()
        {
            Assert.Contains("at least 3", PlanPolygon.Validate(P(0, 0, 1, 1), "points"));
        }
    }
}
