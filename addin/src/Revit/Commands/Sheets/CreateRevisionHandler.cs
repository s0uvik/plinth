// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Sheets/CreateRevisionHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: failures are errors rather than a success with created=false; the result also reports
// issued_to / issued_by; the camelCase aliases are gone.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Sheets
{
    public sealed class CreateRevisionHandler : IRevitCommand
    {
        public string Name => "create_revision";
        public string Description =>
            "Create a project revision with a description and optional date (free text), issued_to, issued_by. "
            + "issued (default false) locks it; an issued revision can no longer be added to or removed from sheets.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""description"":{""type"":""string""},
            ""date"":{""type"":""string""},
            ""issued_to"":{""type"":""string""},
            ""issued_by"":{""type"":""string""},
            ""issued"":{""type"":""boolean""}},
            ""required"":[""description""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var description = p.Value<string>("description");
            if (string.IsNullOrWhiteSpace(description)) return CommandResult.Fail("INVALID_PARAMS: description cannot be empty.");
            var date = p.Value<string>("date");
            var issuedTo = p.Value<string>("issued_to");
            var issuedBy = p.Value<string>("issued_by");
            var issued = p.Value<bool?>("issued") ?? false;

            using (var tx = new Transaction(doc, "B2M: Create Revision"))
            {
                tx.Start();
                try
                {
                    var rev = Revision.Create(doc);
                    rev.Description = description;
                    if (!string.IsNullOrEmpty(date)) rev.RevisionDate = date;
                    if (!string.IsNullOrEmpty(issuedTo)) rev.IssuedTo = issuedTo;
                    if (!string.IsNullOrEmpty(issuedBy)) rev.IssuedBy = issuedBy;
                    // Issued locks the revision, so it is set last.
                    if (issued) rev.Issued = true;
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    return CommandResult.Ok(new
                    {
                        revision_id = RevitCompat.GetId(rev.Id),
                        sequence_number = rev.SequenceNumber,
                        revision_number = rev.RevisionNumber,
                        date = rev.RevisionDate,
                        description = rev.Description,
                        issued_to = rev.IssuedTo,
                        issued_by = rev.IssuedBy,
                        issued = rev.Issued
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the revision: " + ex.Message + " Nothing was created.");
                }
            }
        }
    }
}
