// New in Plinth. "Duplicate before edit": a type (any ElementType), a material or a loaded family is shared, so changing
// it changes every element that uses it. No command may change one unless the caller explicitly chose either
//   duplicate_as: "<new name>"  - copy it, change only the copy, and move only the elements the command names onto it;
//   confirm_edit_all: N         - change it in place, where N must equal the current number of users exactly.
// Without either the command refuses before changing anything (TYPE_IN_USE / MATERIAL_IN_USE / FAMILY_IN_USE), even
// when nothing uses it yet (then confirm_edit_all: 0 is the explicit acknowledgement).
// This file is the Revit-free decision: given what is shared, how many use it and the two request fields, it returns
// copy / in place / refuse with the exact message and data. Counting users, checking that a copy name is free in the
// document, duplicating and moving elements are the caller's job (addin/src/Revit/Commands/TypeEditGuard.cs).
//
// Field forms. When a call changes one shared thing: duplicate_as is a string, confirm_edit_all an integer (materials
// whose users cannot be counted take the string "all"). When a call changes several, both take an object keyed by
// the thing's element id: {"<id>": "<new name>"} and {"<id>": N}; a bare string or integer is then refused, because
// one name or one count cannot be meant for several things.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Core.Safety
{
    public enum SharedKind
    {
        Type,
        Material,
        Family
    }

    public enum SharedEditAction
    {
        Refuse,
        EditCopy,
        EditInPlace
    }

    /// <summary>One shared thing a command wants to change, and who uses it.</summary>
    public sealed class SharedThing
    {
        public SharedKind Kind { get; set; }
        public long Id { get; set; }
        public string Name { get; set; }

        /// <summary>Types: the family (or system family such as "Basic Wall"); empty when there is none.</summary>
        public string FamilyName { get; set; }

        /// <summary>Exact (types, families) or best-effort (materials) number of users; null when they cannot be counted.</summary>
        public int? UserCount { get; set; }

        public IList<long> SampleUserIds { get; set; } = new List<long>();

        /// <summary>Extra counting facts merged into refusal data (materials: element_count, type_count, counted).</summary>
        public JObject CountDetail { get; set; }

        /// <summary>False when the command cannot work on a copy (reloading a family, updating materials named by a file).</summary>
        public bool CopyAllowed { get; set; } = true;
    }

    public sealed class SharedEditDecision
    {
        public SharedEditAction Action { get; private set; }

        /// <summary>The copy's name when Action is EditCopy.</summary>
        public string CopyName { get; private set; }

        /// <summary>"CODE: message" when Action is Refuse.</summary>
        public string Error { get; private set; }

        public JObject Data { get; private set; }

        public string Code => Error == null ? null : Error.Substring(0, Math.Max(0, Error.IndexOf(':')));

        internal static SharedEditDecision Copy(string name) => new SharedEditDecision { Action = SharedEditAction.EditCopy, CopyName = name };

        internal static SharedEditDecision InPlace() => new SharedEditDecision { Action = SharedEditAction.EditInPlace };

        internal static SharedEditDecision Refuse(string error, JObject data = null) =>
            new SharedEditDecision { Action = SharedEditAction.Refuse, Error = error, Data = data };
    }

    public static class SharedEditRule
    {
        public const string DuplicateAsField = "duplicate_as";
        public const string ConfirmField = "confirm_edit_all";
        public const int MaxSamples = 20;

        /// <summary>Schema properties for commands that may change a shared type or material (no leading or trailing comma).</summary>
        public const string SchemaFields =
            @"""duplicate_as"":{""description"":""Change a COPY: the new type/material name (or {\""<id>\"": \""name\""} when the call changes several); the elements this call names move onto the copy.""},"
            + @"""confirm_edit_all"":{""description"":""Change it IN PLACE for every user: the exact number of elements using it (or {\""<id>\"": N} when the call changes several).""}";

        /// <summary>Schema property for commands that can only change in place (no copy is possible).</summary>
        public const string ConfirmOnlySchemaField =
            @"""confirm_edit_all"":{""description"":""Change it IN PLACE for every user: the exact number of elements using it (or {\""<id>\"": N} when the call changes several).""}";

        /// <summary>Sentence for command descriptions.</summary>
        public const string RuleText =
            " Duplicate before edit: a type or material used by other elements is never changed unless you pass duplicate_as (change a copy and "
            + "move only the named elements onto it) or confirm_edit_all: N (N = its exact current user count, to change all of them); "
            + "without either it refuses with TYPE_IN_USE / MATERIAL_IN_USE and the count.";

        /// <summary>
        /// The decision for one shared thing. thingsInCall is how many shared things the whole call changes; a bare
        /// string or integer is accepted only when it is 1. Name-taken checks in the document are the caller's.
        /// </summary>
        public static SharedEditDecision Decide(SharedThing thing, JToken duplicateAs, JToken confirmEditAll, int thingsInCall)
        {
            if (thing == null) throw new ArgumentNullException(nameof(thing));
            var key = thing.Id.ToString(CultureInfo.InvariantCulture);

            if (!TryCopyName(thing, key, duplicateAs, thingsInCall, out var copyName, out var refusal)) return refusal;
            if (!TryConfirm(thing, key, confirmEditAll, thingsInCall, out var confirm, out refusal)) return refusal;

            if (copyName != null && confirm != null)
                return SharedEditDecision.Refuse("INVALID_PARAMS: pass duplicate_as or confirm_edit_all for " + Describe(thing) + ", not both. Nothing was changed.");

            if (copyName != null)
            {
                if (string.Equals(copyName, thing.Name, StringComparison.OrdinalIgnoreCase))
                    return SharedEditDecision.Refuse("NAME_IN_USE: '" + copyName + "' is the name of " + Describe(thing)
                        + " itself; duplicate_as needs a new name. Nothing was changed.", Identity(thing));
                return SharedEditDecision.Copy(copyName);
            }

            if (confirm != null)
            {
                var expected = thing.UserCount.HasValue ? (JToken)thing.UserCount.Value : "all";
                if (JToken.DeepEquals(expected, confirm)) return SharedEditDecision.InPlace();
                var data = Identity(thing);
                data["expected"] = expected;
                data["given"] = confirm;
                var usedBy = thing.UserCount.HasValue
                    ? "is used by " + Count(thing.UserCount.Value, Unit(thing)) + ", not " + confirm.ToString(Newtonsoft.Json.Formatting.None)
                    : "has users that could not be counted";
                return SharedEditDecision.Refuse("EDIT_COUNT_MISMATCH: " + Describe(thing) + " " + usedBy + ". Nothing was changed. Pass "
                    + ConfirmExample(thing, thingsInCall, expected.ToString(Newtonsoft.Json.Formatting.None)) + " to change every one"
                    + (thing.CopyAllowed ? ", or duplicate_as to change a copy." : "."), data);
            }

            return InUse(thing, thingsInCall);
        }

        // ---- field parsing ------------------------------------------------------------------

        private static bool TryCopyName(SharedThing thing, string key, JToken token, int thingsInCall, out string name, out SharedEditDecision refusal)
        {
            name = null;
            refusal = null;
            if (IsAbsent(token)) return true;
            JToken value = token;
            if (token.Type == JTokenType.Object)
            {
                value = ((JObject)token)[key];
                if (IsAbsent(value)) return true;
            }
            else if (token.Type == JTokenType.String && thingsInCall > 1)
            {
                refusal = SharedEditDecision.Refuse("INVALID_PARAMS: this call changes " + thingsInCall + " shared types/materials, so one duplicate_as name "
                    + "cannot be meant for all of them. Pass duplicate_as as {\"<id>\": \"<new name>\", ...} or change one per call. Nothing was changed.");
                return false;
            }
            if (!thing.CopyAllowed)
            {
                refusal = SharedEditDecision.Refuse("INVALID_PARAMS: this command cannot work on a copy of " + Describe(thing)
                    + "; only confirm_edit_all applies. Nothing was changed.");
                return false;
            }
            if (value.Type != JTokenType.String)
            {
                refusal = SharedEditDecision.Refuse("INVALID_PARAMS: duplicate_as must be the new name (text), or {\"<id>\": \"<new name>\"} when the call "
                    + "changes several types/materials. Nothing was changed.");
                return false;
            }
            name = (value.Value<string>() ?? "").Trim();
            if (name.Length > 0) return true;
            refusal = SharedEditDecision.Refuse("INVALID_PARAMS: duplicate_as must not be empty. Nothing was changed.");
            return false;
        }

        private static bool TryConfirm(SharedThing thing, string key, JToken token, int thingsInCall, out JToken confirm, out SharedEditDecision refusal)
        {
            confirm = null;
            refusal = null;
            if (IsAbsent(token)) return true;
            JToken value = token;
            if (token.Type == JTokenType.Object)
            {
                value = ((JObject)token)[key];
                if (IsAbsent(value)) return true;
            }
            else if (thingsInCall > 1)
            {
                refusal = SharedEditDecision.Refuse("INVALID_PARAMS: this call changes " + thingsInCall + " shared types/materials, so one confirm_edit_all "
                    + "count cannot be meant for all of them. Pass confirm_edit_all as {\"<id>\": N, ...} or change one per call. Nothing was changed.");
                return false;
            }
            if (value.Type == JTokenType.Integer)
            {
                confirm = new JValue(value.Value<long>());
                return true;
            }
            if (value.Type == JTokenType.String && string.Equals((value.Value<string>() ?? "").Trim(), "all", StringComparison.OrdinalIgnoreCase))
            {
                confirm = new JValue("all");
                return true;
            }
            refusal = SharedEditDecision.Refuse("INVALID_PARAMS: confirm_edit_all must be the exact number of elements using it (an integer)"
                + (thing.Kind == SharedKind.Material ? ", or \"all\" for a material whose users cannot be counted" : "")
                + ", or {\"<id>\": N} when the call changes several. Nothing was changed.");
            return false;
        }

        private static bool IsAbsent(JToken token) => token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined;

        // ---- messages -----------------------------------------------------------------------

        private static SharedEditDecision InUse(SharedThing thing, int thingsInCall)
        {
            var data = Identity(thing);
            var count = thing.UserCount.HasValue ? (JToken)thing.UserCount.Value : JValue.CreateNull();
            data[thing.Kind == SharedKind.Material ? "user_count" : "instance_count"] = count;
            data["sample_element_ids"] = new JArray((thing.SampleUserIds ?? new List<long>()).Take(MaxSamples));
            if (thing.CountDetail != null)
                foreach (var property in thing.CountDetail.Properties())
                    data[property.Name] = property.Value.DeepClone();

            var expected = thing.UserCount.HasValue ? thing.UserCount.Value.ToString(CultureInfo.InvariantCulture) : "\"all\"";
            var confirm = ConfirmExample(thing, thingsInCall, expected);
            string usedBy;
            if (!thing.UserCount.HasValue) usedBy = "has users that could not be counted";
            else if (thing.UserCount.Value == 0) usedBy = "is used by no " + Unit(thing) + "s yet";
            else usedBy = "is used by " + Count(thing.UserCount.Value, Unit(thing)) + (thing.Kind == SharedKind.Material ? " (best-effort count)" : "");

            var none = thing.UserCount == 0;
            string verb, effect;
            switch (thing.Kind)
            {
                case SharedKind.Family:
                    verb = "FAMILY_IN_USE";
                    effect = (none ? "reloading it changes every element placed with it later" : "reloading it changes all of them")
                        + ". Load the file under a new family name to keep the loaded family, or pass " + confirm + " to reload it.";
                    break;
                case SharedKind.Material:
                    verb = "MATERIAL_IN_USE";
                    effect = (none ? "editing it changes everything that uses it later" : "editing it changes all of them") + ". "
                        + (thing.CopyAllowed ? "Pass duplicate_as to change a copy, or " : "Pass ") + confirm
                        + (none ? " to edit it in place." : " to change every one.");
                    break;
                default:
                    verb = "TYPE_IN_USE";
                    effect = (none ? "editing it changes every element placed with it later" : "editing it changes all of them") + ". "
                        + (thing.CopyAllowed ? "Pass duplicate_as to change a copy (and the elements you name), or " : "Pass ") + confirm
                        + (none ? " to edit it in place." : " to change every one.");
                    break;
            }
            return SharedEditDecision.Refuse(verb + ": " + Describe(thing) + " " + usedBy + "; " + effect, data);
        }

        private static string ConfirmExample(SharedThing thing, int thingsInCall, string expected) =>
            thingsInCall > 1
                ? "confirm_edit_all: {\"" + thing.Id.ToString(CultureInfo.InvariantCulture) + "\": " + expected + ", ...}"
                : "confirm_edit_all: " + expected;

        /// <summary>type 'Family : Type', material 'Name' or family 'Name'.</summary>
        public static string Describe(SharedThing thing)
        {
            switch (thing.Kind)
            {
                case SharedKind.Material: return "material '" + thing.Name + "'";
                case SharedKind.Family: return "family '" + thing.Name + "'";
                default:
                    return "type '" + (string.IsNullOrEmpty(thing.FamilyName) ? thing.Name : thing.FamilyName + " : " + thing.Name) + "'";
            }
        }

        private static string Unit(SharedThing thing) => thing.Kind == SharedKind.Material ? "element or type" : "element";

        private static string Count(int n, string unit) =>
            n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? unit : unit == "element" ? "elements" : "elements or types");

        private static JObject Identity(SharedThing thing)
        {
            switch (thing.Kind)
            {
                case SharedKind.Material:
                    return new JObject { ["material_id"] = thing.Id, ["material_name"] = thing.Name };
                case SharedKind.Family:
                    return new JObject { ["family_id"] = thing.Id, ["family_name"] = thing.Name };
                default:
                    return new JObject { ["type_id"] = thing.Id, ["type_name"] = thing.Name, ["family_name"] = thing.FamilyName ?? "" };
            }
        }
    }
}
