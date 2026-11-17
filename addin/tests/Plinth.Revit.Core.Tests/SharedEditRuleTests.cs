using Plinth.Revit.Core.Safety;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Plinth.Revit.Core.Tests
{
    public class SharedEditRuleTests
    {
        private static SharedThing Column(int? users = 100) => new SharedThing
        {
            Kind = SharedKind.Type,
            Id = 4242,
            Name = "230 x 450mm",
            FamilyName = "M_Concrete-Rectangular-Column",
            UserCount = users,
            SampleUserIds = new long[] { 1, 2, 3 }
        };

        private static SharedThing Brick(int? users = 7) => new SharedThing
        {
            Kind = SharedKind.Material,
            Id = 77,
            Name = "Brick",
            UserCount = users,
            CountDetail = new JObject { ["element_count"] = 5, ["type_count"] = 2 }
        };

        [Fact]
        public void RefusesWithoutAChoice()
        {
            var d = SharedEditRule.Decide(Column(), null, null, 1);
            Assert.Equal(SharedEditAction.Refuse, d.Action);
            Assert.Equal("TYPE_IN_USE", d.Code);
            Assert.Equal("TYPE_IN_USE: type 'M_Concrete-Rectangular-Column : 230 x 450mm' is used by 100 elements; editing it changes all of them. "
                + "Pass duplicate_as to change a copy (and the elements you name), or confirm_edit_all: 100 to change every one.", d.Error);
            Assert.Equal(4242, d.Data.Value<long>("type_id"));
            Assert.Equal("230 x 450mm", d.Data.Value<string>("type_name"));
            Assert.Equal("M_Concrete-Rectangular-Column", d.Data.Value<string>("family_name"));
            Assert.Equal(100, d.Data.Value<int>("instance_count"));
            Assert.Equal(new long[] { 1, 2, 3 }, d.Data["sample_element_ids"].ToObject<long[]>());
        }

        [Fact]
        public void NullTokensCountAsAbsent()
        {
            var d = SharedEditRule.Decide(Column(), JValue.CreateNull(), JValue.CreateNull(), 1);
            Assert.Equal("TYPE_IN_USE", d.Code);
        }

        [Fact]
        public void SamplesAreCappedAtTwenty()
        {
            var thing = Column();
            var many = new long[50];
            for (var i = 0; i < many.Length; i++) many[i] = i;
            thing.SampleUserIds = many;
            var d = SharedEditRule.Decide(thing, null, null, 1);
            Assert.Equal(20, ((JArray)d.Data["sample_element_ids"]).Count);
        }

        [Fact]
        public void DuplicateAsGivesACopy()
        {
            var d = SharedEditRule.Decide(Column(), "  230 x 500mm ", null, 1);
            Assert.Equal(SharedEditAction.EditCopy, d.Action);
            Assert.Equal("230 x 500mm", d.CopyName);
            Assert.Null(d.Error);
        }

        [Fact]
        public void ExactCountEditsInPlace()
        {
            var d = SharedEditRule.Decide(Column(), null, 100, 1);
            Assert.Equal(SharedEditAction.EditInPlace, d.Action);
        }

        [Theory]
        [InlineData(99)]
        [InlineData(101)]
        [InlineData(0)]
        [InlineData(-1)]
        public void WrongCountIsAMismatch(int given)
        {
            var d = SharedEditRule.Decide(Column(), null, given, 1);
            Assert.Equal(SharedEditAction.Refuse, d.Action);
            Assert.Equal("EDIT_COUNT_MISMATCH", d.Code);
            Assert.Equal(100, d.Data.Value<int>("expected"));
            Assert.Equal(given, d.Data.Value<int>("given"));
            Assert.Contains("is used by 100 elements, not " + given, d.Error);
            Assert.Contains("Nothing was changed", d.Error);
        }

        [Fact]
        public void AllIsNotACountForTypes()
        {
            var d = SharedEditRule.Decide(Column(), null, "all", 1);
            Assert.Equal("EDIT_COUNT_MISMATCH", d.Code);
            Assert.Equal("all", d.Data.Value<string>("given"));
        }

        [Fact]
        public void UnusedTypeStillNeedsAChoice()
        {
            var refused = SharedEditRule.Decide(Column(0), null, null, 1);
            Assert.Equal("TYPE_IN_USE", refused.Code);
            Assert.Equal(0, refused.Data.Value<int>("instance_count"));
            Assert.Contains("is used by no elements yet", refused.Error);
            Assert.Contains("confirm_edit_all: 0", refused.Error);

            Assert.Equal(SharedEditAction.EditInPlace, SharedEditRule.Decide(Column(0), null, 0, 1).Action);
            Assert.Equal(SharedEditAction.EditCopy, SharedEditRule.Decide(Column(0), "Copy", null, 1).Action);
        }

        [Fact]
        public void BothChoicesAreRefused()
        {
            var d = SharedEditRule.Decide(Column(), "Copy", 100, 1);
            Assert.Equal("INVALID_PARAMS", d.Code);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void EmptyCopyNameIsRefused(string name)
        {
            Assert.Equal("INVALID_PARAMS", SharedEditRule.Decide(Column(), name, null, 1).Code);
        }

        [Fact]
        public void CopyNamedLikeTheOriginalIsNameInUse()
        {
            var d = SharedEditRule.Decide(Column(), "230 X 450MM", null, 1);
            Assert.Equal("NAME_IN_USE", d.Code);
        }

        [Fact]
        public void WrongShapesAreRefused()
        {
            Assert.Equal("INVALID_PARAMS", SharedEditRule.Decide(Column(), 5, null, 1).Code);
            Assert.Equal("INVALID_PARAMS", SharedEditRule.Decide(Column(), null, "100", 1).Code);
            Assert.Equal("INVALID_PARAMS", SharedEditRule.Decide(Column(), null, 100.5, 1).Code);
            Assert.Equal("INVALID_PARAMS", SharedEditRule.Decide(Column(), null, true, 1).Code);
        }

        [Fact]
        public void SeveralThingsNeedTheObjectForm()
        {
            Assert.Equal("INVALID_PARAMS", SharedEditRule.Decide(Column(), "Copy", null, 2).Code);
            Assert.Equal("INVALID_PARAMS", SharedEditRule.Decide(Column(), null, 100, 2).Code);

            var copy = SharedEditRule.Decide(Column(), new JObject { ["4242"] = "Copy", ["9"] = "Other" }, null, 2);
            Assert.Equal(SharedEditAction.EditCopy, copy.Action);
            Assert.Equal("Copy", copy.CopyName);

            var inPlace = SharedEditRule.Decide(Column(), null, new JObject { ["4242"] = 100 }, 2);
            Assert.Equal(SharedEditAction.EditInPlace, inPlace.Action);

            var missing = SharedEditRule.Decide(Column(), new JObject { ["9"] = "Other" }, new JObject { ["9"] = 3 }, 2);
            Assert.Equal("TYPE_IN_USE", missing.Code);
            Assert.Contains("confirm_edit_all: {\"4242\": 100, ...}", missing.Error);

            var mismatch = SharedEditRule.Decide(Column(), null, new JObject { ["4242"] = 3 }, 2);
            Assert.Equal("EDIT_COUNT_MISMATCH", mismatch.Code);
        }

        [Fact]
        public void ObjectFormWorksForOneThingToo()
        {
            Assert.Equal(SharedEditAction.EditInPlace, SharedEditRule.Decide(Column(), null, new JObject { ["4242"] = 100 }, 1).Action);
        }

        [Fact]
        public void MaterialRefusalCarriesTheCount()
        {
            var d = SharedEditRule.Decide(Brick(), null, null, 1);
            Assert.Equal("MATERIAL_IN_USE", d.Code);
            Assert.StartsWith("MATERIAL_IN_USE: material 'Brick' is used by 7 elements or types (best-effort count)", d.Error);
            Assert.Contains("confirm_edit_all: 7", d.Error);
            Assert.Equal(77, d.Data.Value<long>("material_id"));
            Assert.Equal("Brick", d.Data.Value<string>("material_name"));
            Assert.Equal(7, d.Data.Value<int>("user_count"));
            Assert.Equal(5, d.Data.Value<int>("element_count"));
            Assert.Equal(2, d.Data.Value<int>("type_count"));
            Assert.Equal(SharedEditAction.EditInPlace, SharedEditRule.Decide(Brick(), null, 7, 1).Action);
            Assert.Equal(SharedEditAction.EditCopy, SharedEditRule.Decide(Brick(), "Brick 2", null, 1).Action);
        }

        [Fact]
        public void UncountableMaterialNeedsAll()
        {
            var refused = SharedEditRule.Decide(Brick(null), null, null, 1);
            Assert.Equal("MATERIAL_IN_USE", refused.Code);
            Assert.Contains("confirm_edit_all: \"all\"", refused.Error);
            Assert.Equal(JTokenType.Null, refused.Data["user_count"].Type);

            Assert.Equal(SharedEditAction.EditInPlace, SharedEditRule.Decide(Brick(null), null, "all", 1).Action);
            Assert.Equal(SharedEditAction.EditInPlace, SharedEditRule.Decide(Brick(null), null, " ALL ", 1).Action);
            var number = SharedEditRule.Decide(Brick(null), null, 7, 1);
            Assert.Equal("EDIT_COUNT_MISMATCH", number.Code);
            Assert.Equal("all", number.Data.Value<string>("expected"));
        }

        [Fact]
        public void CountedMaterialRefusesAll()
        {
            Assert.Equal("EDIT_COUNT_MISMATCH", SharedEditRule.Decide(Brick(), null, "all", 1).Code);
        }

        [Fact]
        public void CopyCanBeForbidden()
        {
            var family = new SharedThing { Kind = SharedKind.Family, Id = 5, Name = "Door", UserCount = 12, CopyAllowed = false };
            var refused = SharedEditRule.Decide(family, null, null, 1);
            Assert.Equal("FAMILY_IN_USE", refused.Code);
            Assert.DoesNotContain("duplicate_as", refused.Error);
            Assert.Contains("confirm_edit_all: 12", refused.Error);
            Assert.Equal(12, refused.Data.Value<int>("instance_count"));
            Assert.Equal("INVALID_PARAMS", SharedEditRule.Decide(family, "Door 2", null, 1).Code);
            Assert.Equal(SharedEditAction.EditInPlace, SharedEditRule.Decide(family, null, 12, 1).Action);
        }

        [Fact]
        public void SystemTypeWithoutFamilyIsNamedAlone()
        {
            var thing = new SharedThing { Kind = SharedKind.Type, Id = 1, Name = "Generic 200", FamilyName = "", UserCount = 1 };
            var d = SharedEditRule.Decide(thing, null, null, 1);
            Assert.StartsWith("TYPE_IN_USE: type 'Generic 200' is used by 1 element;", d.Error);
        }

        [Fact]
        public void SchemaFieldsAreValidJson()
        {
            var schema = JObject.Parse("{\"type\":\"object\",\"properties\":{" + SharedEditRule.SchemaFields + "}}");
            Assert.NotNull(schema["properties"]["duplicate_as"]);
            Assert.NotNull(schema["properties"]["confirm_edit_all"]);
            var confirmOnly = JObject.Parse("{\"properties\":{" + SharedEditRule.ConfirmOnlySchemaField + "}}");
            Assert.Null(confirmOnly["properties"]["duplicate_as"]);
        }

        [Fact]
        public void SchemaAcceptsEveryFieldForm()
        {
            var schema = "{\"type\":\"object\",\"properties\":{" + SharedEditRule.SchemaFields + "}}";
            Assert.True(Plinth.Revit.Core.Execution.SchemaValidator.Validate(schema, "{\"duplicate_as\":\"x\",\"confirm_edit_all\":3}").IsValid);
            Assert.True(Plinth.Revit.Core.Execution.SchemaValidator.Validate(schema, "{\"duplicate_as\":{\"1\":\"x\"},\"confirm_edit_all\":{\"1\":3}}").IsValid);
            Assert.True(Plinth.Revit.Core.Execution.SchemaValidator.Validate(schema, "{\"confirm_edit_all\":\"all\"}").IsValid);
        }
    }
}
