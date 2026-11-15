// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// files src/shared/Infrastructure/SafeFailuresPreprocessor.cs and src/shared/Failures/FailurePolicy.cs, commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for B2M: one application-level FailuresProcessing handler applies the default rvt-mcp policy
// (any warning or error rolls the transaction back) while a request runs, and records every message
// for the response and log. rvt-mcp's failure ledger, rules, grants and dialog adapters are not ported
// here; they come with the failure-record tools.

using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Bridge
{
    /// <summary>
    /// Active while one request runs on Revit's main thread. Any Revit warning or error raised by
    /// that request's transactions rolls the transaction back (PRD: warnings roll back by default;
    /// the layout engine must produce warning-free geometry). Outside a scope, Revit behaves normally.
    /// </summary>
    internal sealed class FailureScope : System.IDisposable
    {
        private const int MaxRecorded = 200;

        private static FailureScope _active;
        private readonly List<JObject> _messages = new List<JObject>();

        private FailureScope() { }

        /// <summary>True once any transaction in this scope was rolled back because of a failure.</summary>
        public bool RolledBack { get; private set; }

        public bool HasMessages => _messages.Count > 0;

        public static FailureScope Begin()
        {
            _active = new FailureScope();
            return _active;
        }

        public void Dispose()
        {
            if (ReferenceEquals(_active, this)) _active = null;
        }

        public JArray ToJson() => new JArray(_messages);

        /// <summary>Registered on ControlledApplication.FailuresProcessing for the add-in's lifetime.</summary>
        public static void OnFailuresProcessing(object sender, FailuresProcessingEventArgs e)
        {
            var scope = _active;
            if (scope == null) return; // The user's own edits: leave Revit's normal handling alone.

            var accessor = e.GetFailuresAccessor();
            var failures = accessor.GetFailureMessages();
            if (failures.Count == 0) return;

            foreach (var failure in failures)
            {
                if (scope._messages.Count >= MaxRecorded) break;
                scope._messages.Add(new JObject
                {
                    ["severity"] = failure.GetSeverity().ToString(),
                    ["description"] = failure.GetDescriptionText(),
                    ["failure_id"] = failure.GetFailureDefinitionId().Guid.ToString(),
                    ["element_ids"] = new JArray(failure.GetFailingElementIds().Select(RevitCompat.GetId))
                });
            }

            var options = accessor.GetFailureHandlingOptions();
            options.SetClearAfterRollback(true);
            accessor.SetFailureHandlingOptions(options);
            e.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
            scope.RolledBack = true;
        }
    }
}
