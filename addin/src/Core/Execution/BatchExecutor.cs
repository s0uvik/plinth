// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Runtime/BatchExecutor.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: the list of commands that must run directly is one table; each sub-command's
// params are validated against its schema before anything runs, so a typo in sub-command 19 no
// longer rolls back 18 finished ones; the failure-policy stop is reported through the invoker.

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Core.Execution
{
    /// <summary>
    /// Iteration and rollback decision for <c>batch_execute</c>, kept free of the Revit API so it
    /// can be unit-tested. The Revit handler owns the TransactionGroup and acts on the outcome.
    /// </summary>
    public static class BatchExecutor
    {
        public const int MaxCommands = 20;

        /// <summary>Commands that must be called directly, never inside a batch.</summary>
        private static readonly Dictionary<string, string> Direct = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["batch_execute"] = "Nested batch_execute is not supported.",
            ["health"] = "health is answered by the transport; call it directly.",
            ["send_code_to_revit"] = "send_code_to_revit cannot run inside batch_execute; call it directly.",
            ["run_baked_tool"] = "run_baked_tool cannot run inside batch_execute; call it directly.",
            ["report_task_result"] = "report_task_result cannot run inside batch_execute; call it directly.",
            ["open_model"] = "open_model cannot run inside batch_execute; open and confirm the document first.",
            ["save_document"] = "save_document cannot run inside batch_execute; call it directly.",
            ["save_checkpoint"] = "save_checkpoint cannot run inside batch_execute; call it directly.",
            ["manage_checkpoints"] = "manage_checkpoints cannot run inside batch_execute; call it directly.",
            // Revit's StairsEditScope cannot start inside a transaction or close while a transaction group is open.
            ["create_stair"] = "create_stair cannot run inside batch_execute (Revit's stair edit mode cannot run inside the batch's "
                + "transaction group); call it directly.",
            // It opens, saves and closes family documents and may write .rfa files, which the batch's rollback cannot undo.
            ["ensure_family_type"] = "ensure_family_type cannot run inside batch_execute (it opens and saves family files, which a batch "
                + "rollback cannot undo); call it directly before the batch.",
            ["create_box_family"] = "create_box_family cannot run inside batch_execute (it builds, saves and loads a family file, which a "
                + "batch rollback cannot undo); call it directly before the batch.",
            // Both open and close another Revit file in the background, which is not done inside the batch's transaction group.
            ["list_types_in_file"] = "list_types_in_file cannot run inside batch_execute (it opens another Revit file); call it directly.",
            ["copy_types_from_file"] = "copy_types_from_file cannot run inside batch_execute (it opens another Revit file and copies "
                + "types from it); call it directly before the batch.",
        };

        public sealed class Outcome
        {
            public JArray Results { get; } = new JArray();
            public bool AnyFailed { get; set; }

            /// <summary>Index of the first failed sub-command, or -1.</summary>
            public int FirstFailedIndex { get; set; } = -1;

            public string FirstError { get; set; }

            public bool ShouldRollBack(bool continueOnError) => AnyFailed && !continueOnError;
        }

        public sealed class InvokeResult
        {
            public bool Success { get; private set; }
            public JToken Data { get; private set; }
            public string Error { get; private set; }

            public static InvokeResult Ok(JToken data) => new InvokeResult { Success = true, Data = data };

            public static InvokeResult Fail(string error, JToken data = null) =>
                new InvokeResult { Success = false, Error = error, Data = data };
        }

        public static string TooManyCommandsMessage(int count) =>
            "BATCH_TOO_LARGE: batch_execute runs at most " + MaxCommands + " commands (got " + count + "). Split the batch.";

        /// <summary>
        /// Validate every sub-command before any runs. Returns null when all are valid, otherwise an
        /// outcome describing the first problem.
        /// </summary>
        /// <param name="schemaOf">Returns a command's schema, or null if the command does not exist.</param>
        public static Outcome Precheck(JArray commands, Func<string, string> schemaOf)
        {
            if (commands == null) throw new ArgumentNullException(nameof(commands));
            if (schemaOf == null) throw new ArgumentNullException(nameof(schemaOf));

            if (commands.Count > MaxCommands) return Failed(-1, TooManyCommandsMessage(commands.Count));

            for (var i = 0; i < commands.Count; i++)
            {
                var cmd = commands[i] as JObject;
                var name = cmd?.Value<string>("command");
                if (string.IsNullOrEmpty(name)) return Failed(i, "INVALID_PARAMS: sub-command " + i + " has no 'command'.");
                if (Direct.TryGetValue(name, out var reason)) return Failed(i, "NOT_ALLOWED_IN_BATCH: " + reason);
                var schema = schemaOf(name);
                if (schema == null) return Failed(i, "UNKNOWN_COMMAND: " + name + ".");
                var validation = SchemaValidator.Validate(schema, ParamsOf(cmd));
                if (!validation.IsValid)
                    return Failed(i, "Sub-command " + i + " (" + name + "): " + validation.Error
                        + (validation.Suggestion == null ? "" : " " + validation.Suggestion));
            }
            return null;
        }

        /// <summary>Run the sub-commands in order. Stops at the first failure unless continueOnError.</summary>
        public static Outcome Run(JArray commands, bool continueOnError, Func<string, string, InvokeResult> invoke)
        {
            if (commands == null) throw new ArgumentNullException(nameof(commands));
            if (invoke == null) throw new ArgumentNullException(nameof(invoke));

            var outcome = new Outcome();
            for (var i = 0; i < commands.Count; i++)
            {
                var cmd = (JObject)commands[i];
                var name = cmd.Value<string>("command");
                InvokeResult result;
                try
                {
                    result = invoke(name, ParamsOf(cmd));
                }
                catch (Exception ex)
                {
                    result = InvokeResult.Fail(ex.Message);
                }

                var failed = 0;
                if (result.Success && !HasPartialFailure(result.Data, out failed))
                {
                    outcome.Results.Add(new JObject { ["index"] = i, ["command"] = name, ["ok"] = true, ["data"] = result.Data });
                    continue;
                }

                var error = result.Success ? "Command completed with " + failed + " partial failure(s)." : result.Error;
                var entry = new JObject { ["index"] = i, ["command"] = name, ["ok"] = false, ["error"] = error };
                if (result.Data != null) entry["data"] = result.Data;
                outcome.Results.Add(entry);
                if (!outcome.AnyFailed)
                {
                    outcome.AnyFailed = true;
                    outcome.FirstFailedIndex = i;
                    outcome.FirstError = error;
                }
                if (!continueOnError) break;
            }
            return outcome;
        }

        private static Outcome Failed(int index, string error)
        {
            var outcome = new Outcome { AnyFailed = true, FirstFailedIndex = index, FirstError = error };
            outcome.Results.Add(new JObject { ["index"] = index, ["ok"] = false, ["error"] = error });
            return outcome;
        }

        private static string ParamsOf(JObject cmd)
        {
            var p = cmd["params"];
            return p == null || p.Type == JTokenType.Null ? "{}" : p.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static bool HasPartialFailure(JToken data, out int failedCount)
        {
            failedCount = 0;
            var token = (data as JObject)?["failedCount"];
            if (token == null || token.Type != JTokenType.Integer) return false;
            failedCount = token.Value<int>();
            return failedCount > 0;
        }
    }
}
