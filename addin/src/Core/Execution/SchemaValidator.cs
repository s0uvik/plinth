// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Infrastructure/SchemaValidator.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: unknown fields are rejected with the list of valid ones (a typo such as "startx"
// used to be ignored silently); enums are checked for numbers too; malformed schemas fail closed.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Core.Execution
{
    public sealed class SchemaValidationResult
    {
        public bool IsValid { get; private set; }
        public string Error { get; private set; }
        public string Suggestion { get; private set; }

        public static SchemaValidationResult Ok() => new SchemaValidationResult { IsValid = true };

        public static SchemaValidationResult Fail(string error, string suggestion) =>
            new SchemaValidationResult { IsValid = false, Error = error, Suggestion = suggestion };
    }

    /// <summary>
    /// Checks request params against a command's schema before dispatch. Supports the JSON Schema
    /// subset the handlers use: type, required, enum, properties, array items.type and
    /// additionalProperties (false by default: unknown fields are errors).
    /// </summary>
    public static class SchemaValidator
    {
        public static SchemaValidationResult Validate(string schemaJson, string paramsJson)
        {
            JObject schema;
            try
            {
                schema = string.IsNullOrWhiteSpace(schemaJson) ? new JObject() : JObject.Parse(schemaJson);
            }
            catch (JsonException)
            {
                return SchemaValidationResult.Fail("INVALID_SCHEMA: this command's schema is malformed; this is an add-in bug.", null);
            }

            JObject parameters;
            try
            {
                var token = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JToken.Parse(paramsJson);
                parameters = token as JObject;
                if (parameters == null)
                    return SchemaValidationResult.Fail("INVALID_PARAMS: params must be a JSON object.", "Pass params as {\"field\": value}.");
            }
            catch (JsonException ex)
            {
                return SchemaValidationResult.Fail("INVALID_PARAMS: params must be a JSON object: " + ex.Message, "Pass params as {\"field\": value}.");
            }

            var properties = schema["properties"] as JObject ?? new JObject();
            var valid = properties.Properties().Select(p => p.Name).ToList();

            foreach (var required in RequiredFields(schema))
            {
                if (parameters[required] == null || parameters[required].Type == JTokenType.Null)
                    return SchemaValidationResult.Fail(
                        "INVALID_PARAMS: required field '" + required + "' is missing.",
                        "Add " + Example(properties, required) + ". Valid fields: " + string.Join(", ", valid) + ".");
            }

            var allowExtra = schema["additionalProperties"]?.Type == JTokenType.Boolean && schema.Value<bool>("additionalProperties");
            if (!allowExtra)
            {
                foreach (var given in parameters.Properties())
                {
                    if (properties[given.Name] == null)
                        return SchemaValidationResult.Fail(
                            "INVALID_PARAMS: unknown field '" + given.Name + "'.",
                            "Valid fields: " + (valid.Count == 0 ? "(none)" : string.Join(", ", valid)) + ".");
                }
            }

            foreach (var prop in properties.Properties())
            {
                var actual = parameters[prop.Name];
                if (actual == null || actual.Type == JTokenType.Null) continue;
                var propSchema = prop.Value as JObject;
                if (propSchema == null) continue;

                var expectedType = propSchema.Value<string>("type");
                if (!IsTypeMatch(expectedType, actual))
                    return SchemaValidationResult.Fail(
                        "INVALID_PARAMS: field '" + prop.Name + "' must be " + expectedType + ", got " + JsonType(actual) + ".",
                        "Pass " + Example(properties, prop.Name) + ".");

                if (propSchema["enum"] is JArray allowed && allowed.Count > 0
                    && !allowed.Any(a => JToken.DeepEquals(a, actual)))
                    return SchemaValidationResult.Fail(
                        "INVALID_PARAMS: field '" + prop.Name + "' = " + actual.ToString(Formatting.None) + " is not allowed.",
                        "Pick one of: " + string.Join(", ", allowed.Select(a => a.ToString(Formatting.None))) + ".");

                if (expectedType == "array" && actual is JArray array
                    && propSchema["items"] is JObject items && items.Value<string>("type") is string itemType)
                {
                    for (var i = 0; i < array.Count; i++)
                        if (!IsTypeMatch(itemType, array[i]))
                            return SchemaValidationResult.Fail(
                                "INVALID_PARAMS: field '" + prop.Name + "[" + i + "]' must be " + itemType + ", got " + JsonType(array[i]) + ".",
                                "Pass " + prop.Name + " as an array of " + itemType + ".");
                }
            }

            return SchemaValidationResult.Ok();
        }

        private static IEnumerable<string> RequiredFields(JObject schema) =>
            (schema["required"] as JArray ?? new JArray()).Select(t => t.Value<string>()).Where(s => !string.IsNullOrEmpty(s));

        private static bool IsTypeMatch(string expected, JToken token)
        {
            switch (expected)
            {
                case null: return true;
                case "string": return token.Type == JTokenType.String;
                case "number": return token.Type == JTokenType.Float || token.Type == JTokenType.Integer;
                case "integer": return token.Type == JTokenType.Integer;
                case "boolean": return token.Type == JTokenType.Boolean;
                case "array": return token.Type == JTokenType.Array;
                case "object": return token.Type == JTokenType.Object;
                default: return true;
            }
        }

        private static string JsonType(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.String: return "string";
                case JTokenType.Float: return "number";
                case JTokenType.Integer: return "integer";
                case JTokenType.Boolean: return "boolean";
                case JTokenType.Array: return "array";
                case JTokenType.Object: return "object";
                case JTokenType.Null: return "null";
                default: return token.Type.ToString().ToLowerInvariant();
            }
        }

        private static string Example(JObject properties, string field)
        {
            string example;
            switch ((properties[field] as JObject)?.Value<string>("type"))
            {
                case "string": example = "\"value\""; break;
                case "number": example = "3000"; break;
                case "integer": example = "42"; break;
                case "boolean": example = "true"; break;
                case "array": example = "[...]"; break;
                case "object": example = "{...}"; break;
                default: example = "..."; break;
            }
            return "{\"" + field + "\": " + example + "}";
        }
    }
}
