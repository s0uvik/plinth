// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Documents/SetProjectInfoHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: a call that changes nothing (every field missing or read-only) fails and rolls back, so
// batch_execute sees it; field errors use the Plinth "CODE: message" form.

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Meta
{
    public sealed class SetProjectInfoHandler : IRevitCommand
    {
        private static readonly KeyValuePair<string, BuiltInParameter>[] Fields =
        {
            new KeyValuePair<string, BuiltInParameter>("name", BuiltInParameter.PROJECT_NAME),
            new KeyValuePair<string, BuiltInParameter>("number", BuiltInParameter.PROJECT_NUMBER),
            new KeyValuePair<string, BuiltInParameter>("client_name", BuiltInParameter.CLIENT_NAME),
            new KeyValuePair<string, BuiltInParameter>("address", BuiltInParameter.PROJECT_ADDRESS),
            new KeyValuePair<string, BuiltInParameter>("status", BuiltInParameter.PROJECT_STATUS),
            new KeyValuePair<string, BuiltInParameter>("issue_date", BuiltInParameter.PROJECT_ISSUE_DATE)
        };

        public string Name => "set_project_info";
        public string Description => "Set Project Information fields: name, number, client_name, address, status, issue_date (text). "
            + "Pass at least one. Read-only or missing parameters are skipped and listed.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""name"":{""type"":""string""},
            ""number"":{""type"":""string""},
            ""client_name"":{""type"":""string""},
            ""address"":{""type"":""string""},
            ""status"":{""type"":""string""},
            ""issue_date"":{""type"":""string""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject parameters)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (doc.IsFamilyDocument) return CommandResult.Fail("NOT_A_PROJECT: family documents have no Project Information.");

            var supplied = 0;
            foreach (var field in Fields)
                if (parameters.Value<string>(field.Key) != null) supplied++;
            if (supplied == 0)
                return CommandResult.Fail("INVALID_PARAMS: pass at least one of name, number, client_name, address, status, issue_date.");

            var info = doc.ProjectInformation;
            if (info == null) return CommandResult.Fail("NO_PROJECT_INFO: this document has no Project Information element.");

            var changed = new JArray();
            var skipped = new JArray();
            using (var tx = new Transaction(doc, "Plinth: Set project info"))
            {
                tx.Start();
                foreach (var field in Fields)
                {
                    var value = parameters.Value<string>(field.Key);
                    if (value == null) continue;
                    var parameter = info.get_Parameter(field.Value);
                    if (parameter == null)
                    {
                        skipped.Add(new JObject { ["field"] = field.Key, ["reason"] = "parameter_not_found" });
                        continue;
                    }
                    if (parameter.IsReadOnly)
                    {
                        skipped.Add(new JObject { ["field"] = field.Key, ["reason"] = "read_only" });
                        continue;
                    }
                    try
                    {
                        if (parameter.Set(value)) changed.Add(field.Key);
                        else skipped.Add(new JObject { ["field"] = field.Key, ["reason"] = "set_failed" });
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(new JObject { ["field"] = field.Key, ["reason"] = "set_failed", ["detail"] = ex.Message });
                    }
                }

                if (changed.Count == 0)
                {
                    tx.RollBack();
                    return CommandResult.Fail("NOTHING_CHANGED: no field could be set. Nothing changed.", new JObject { ["skipped"] = skipped });
                }
                if (tx.Commit() != TransactionStatus.Committed)
                    return CommandResult.Fail("NOT_COMMITTED: Revit rolled the change back; see failures. Nothing changed.");
            }

            return CommandResult.Ok(new JObject
            {
                ["project_info_id"] = RevitCompat.GetId(info.Id),
                ["supplied"] = supplied,
                ["changed_fields"] = changed,
                ["skipped"] = skipped
            });
        }
    }
}
