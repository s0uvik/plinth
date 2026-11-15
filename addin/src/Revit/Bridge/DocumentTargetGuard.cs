// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// files src/shared/Infrastructure/Execution/DocumentTargetGuard.cs and src/shared/Infrastructure/ToolReadPolicy.cs
// (DocumentRejection), commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: the write rule lives next to the token it checks; messages name the B2M confirm step.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Bridge
{
    /// <summary>
    /// Gives each open document a random token for its lifetime in this Revit session. A client
    /// confirms the document once (any read command returns <c>_target.document_id</c>) and sends that
    /// token with every write. Names and paths are never identities. After a Revit restart every
    /// token changes, so the client must confirm again. Revit main thread only.
    /// </summary>
    internal static class DocumentTargetGuard
    {
        private static readonly Dictionary<Document, string> Tokens = new Dictionary<Document, string>();

        internal static JObject Capture(UIApplication app)
        {
            RemoveClosedDocuments();
            var doc = app.ActiveUIDocument?.Document;
            using (var process = Process.GetCurrentProcess())
                return new JObject
                {
                    ["target_id"] = "revit-" + app.Application.VersionNumber + "-" + process.Id,
                    ["pid"] = process.Id,
                    ["revit_year"] = int.TryParse(app.Application.VersionNumber, out var y) ? new JValue(y) : JValue.CreateNull(),
                    ["document_id"] = TokenFor(doc),
                    ["document_title"] = doc?.Title,
                    ["is_family_document"] = doc == null ? JValue.CreateNull() : new JValue(doc.IsFamilyDocument)
                };
        }

        /// <summary>Null when a write may run, otherwise the error to return. Nothing has run either way.</summary>
        internal static string WriteRejection(string expectedDocumentId, string activeDocumentId)
        {
            if (activeDocumentId == null)
                return "NO_DOCUMENT: no document is open in Revit. Nothing ran.";
            if (expectedDocumentId == null)
                return "DOCUMENT_UNCONFIRMED: writes need expected_document_id. Call a read command such as "
                    + "get_current_view_info, check _target.document_title, and send _target.document_id with each write. Nothing ran.";
            if (!string.Equals(expectedDocumentId, activeDocumentId, StringComparison.Ordinal))
                return "DOCUMENT_CHANGED: the active document is not the one this run confirmed (or Revit restarted). "
                    + "Confirm the document again before writing. Nothing ran.";
            return null;
        }

        private static string TokenFor(Document doc)
        {
            if (doc == null || !doc.IsValidObject) return null;
            if (!Tokens.TryGetValue(doc, out var token)) Tokens.Add(doc, token = Guid.NewGuid().ToString("N"));
            return token;
        }

        private static void RemoveClosedDocuments()
        {
            // Hashing a closed document touches a dead native wrapper; rebuild from live keys instead of Remove.
            var live = Tokens.Where(pair => pair.Key.IsValidObject).ToArray();
            if (live.Length == Tokens.Count) return;
            Tokens.Clear();
            foreach (var pair in live) Tokens.Add(pair.Key, pair.Value);
        }
    }
}
