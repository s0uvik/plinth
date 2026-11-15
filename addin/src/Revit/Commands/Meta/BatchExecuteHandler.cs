// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Runtime/BatchExecuteHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: every sub-command is validated before the TransactionGroup starts; a rolled-back
// batch returns success=false (rvt-mcp returned success with rolledBack=true); the output=file option
// is not ported; failure-policy stops arrive through the sub-command's own result.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using B2M.Revit.Core.Execution;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Meta
{
    /// <summary>
    /// Runs up to 20 commands in one TransactionGroup, assimilated into a single undo step.
    /// Any failure rolls the whole group back unless continueOnError is set.
    /// </summary>
    public sealed class BatchExecuteHandler : IRevitCommand
    {
        private readonly CommandDispatcher _dispatcher;

        public BatchExecuteHandler(CommandDispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public string Name => "batch_execute";
        public string Description => "Run up to 20 commands as one Revit undo step. Any failure rolls all of them back unless continueOnError is true.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""commands"":{""type"":""array"",""items"":{""type"":""object""}},
            ""continueOnError"":{""type"":""boolean""}},""required"":[""commands""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject parameters)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var commands = (JArray)parameters["commands"];
            if (commands.Count == 0) return CommandResult.Fail("INVALID_PARAMS: 'commands' must not be empty.");
            var continueOnError = parameters.Value<bool?>("continueOnError") ?? false;

            var precheck = BatchExecutor.Precheck(commands, name => _dispatcher.Get(name)?.ParametersSchema);
            if (precheck != null)
                return CommandResult.Fail("BATCH_INVALID: " + precheck.FirstError + " Nothing ran.",
                    new JObject { ["results"] = precheck.Results, ["rolledBack"] = false });

            using (var group = new TransactionGroup(doc, "B2M: Batch"))
            {
                group.Start();
                var outcome = BatchExecutor.Run(commands, continueOnError, (name, json) =>
                {
                    var result = _dispatcher.Get(name).Execute(app, JObject.Parse(json));
                    return result.Success ? BatchExecutor.InvokeResult.Ok(result.Data) : BatchExecutor.InvokeResult.Fail(result.Error, result.Data);
                });

                if (outcome.ShouldRollBack(continueOnError))
                {
                    var status = group.RollBack();
                    var data = new JObject { ["results"] = outcome.Results, ["rolledBack"] = status == TransactionStatus.RolledBack };
                    if (status != TransactionStatus.RolledBack)
                        return CommandResult.Fail("BATCH_ROLLBACK_UNCONFIRMED: Revit reported " + status + " when rolling back. Check the model.", data);
                    return CommandResult.Fail("BATCH_ROLLED_BACK: sub-command " + outcome.FirstFailedIndex + " failed: "
                        + outcome.FirstError + " No changes were kept.", data);
                }

                var committed = group.Assimilate();
                if (committed != TransactionStatus.Committed)
                    return CommandResult.Fail("BATCH_NOT_COMMITTED: Revit reported " + committed + ".",
                        new JObject { ["results"] = outcome.Results, ["rolledBack"] = true });
                return CommandResult.Ok(new JObject { ["results"] = outcome.Results, ["rolledBack"] = false });
            }
        }
    }
}
