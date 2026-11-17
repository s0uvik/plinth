using Plinth.Revit.Core.Execution;
using Xunit;

namespace Plinth.Revit.Core.Tests
{
    public class SchemaValidatorTests
    {
        private const string Schema = @"{""type"":""object"",""properties"":{
            ""elevation"":{""type"":""number""},
            ""name"":{""type"":""string""},
            ""kind"":{""type"":""string"",""enum"":[""wall""]},
            ""ids"":{""type"":""array"",""items"":{""type"":""integer""}}},
            ""required"":[""elevation""]}";

        [Fact]
        public void AcceptsValidParams()
        {
            Assert.True(SchemaValidator.Validate(Schema, @"{""elevation"":3000,""name"":""L1"",""kind"":""wall"",""ids"":[1,2]}").IsValid);
        }

        [Fact]
        public void IntegerCountsAsNumber()
        {
            Assert.True(SchemaValidator.Validate(Schema, @"{""elevation"":3000}").IsValid);
            Assert.True(SchemaValidator.Validate(Schema, @"{""elevation"":3000.5}").IsValid);
        }

        [Fact]
        public void RejectsMissingRequiredFieldAndListsValidFields()
        {
            var result = SchemaValidator.Validate(Schema, @"{""name"":""L1""}");
            Assert.False(result.IsValid);
            Assert.Contains("'elevation' is missing", result.Error);
            Assert.Contains("Valid fields: elevation, name, kind, ids", result.Suggestion);
        }

        [Fact]
        public void RejectsUnknownFieldInsteadOfIgnoringIt()
        {
            var result = SchemaValidator.Validate(Schema, @"{""elevation"":0,""elevaton"":3000}");
            Assert.False(result.IsValid);
            Assert.Contains("unknown field 'elevaton'", result.Error);
        }

        [Fact]
        public void AllowsUnknownFieldsWhenSchemaSaysSo()
        {
            Assert.True(SchemaValidator.Validate(@"{""additionalProperties"":true}", @"{""anything"":1}").IsValid);
        }

        [Fact]
        public void RejectsWrongTypeEnumAndArrayItem()
        {
            Assert.Contains("must be number, got string", SchemaValidator.Validate(Schema, @"{""elevation"":""3000""}").Error);
            Assert.Contains("Pick one of: \"wall\"", SchemaValidator.Validate(Schema, @"{""elevation"":0,""kind"":""pipe""}").Suggestion);
            Assert.Contains("'ids[1]' must be integer", SchemaValidator.Validate(Schema, @"{""elevation"":0,""ids"":[1,""x""]}").Error);
        }

        [Fact]
        public void RejectsNonObjectParamsAndMalformedSchema()
        {
            Assert.Contains("INVALID_PARAMS", SchemaValidator.Validate(Schema, "[1,2]").Error);
            Assert.Contains("INVALID_SCHEMA", SchemaValidator.Validate("{not json", "{}").Error);
        }

        [Fact]
        public void EmptySchemaAcceptsEmptyParamsOnly()
        {
            Assert.True(SchemaValidator.Validate("{}", "{}").IsValid);
            Assert.False(SchemaValidator.Validate("{}", @"{""x"":1}").IsValid);
        }
    }
}
