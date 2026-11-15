// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Infrastructure/Execution/McpEventHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: keeps the queue, the stale-request skip, one command per ExternalEvent run with
// re-raise, schema validation before dispatch, the document check, path scrubbing and the _target stamp.
// History, reports, survey, toasts, script protocol, change tracking and response spilling are not
// ported here; the response cap now replaces oversized data with an error that says whether the
// command changed the model.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.UI;
using B2M.Revit.Core.Execution;
using B2M.Revit.Core.Health;
using B2M.Revit.Core.Logging;
using B2M.Revit.Core.Security;
using B2M.Revit.Core.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Bridge
{
    /// <summary>
    /// Receives requests from the transport thread and runs them on Revit's main thread through one
    /// ExternalEvent: one command per run, then raise again while work remains.
    /// </summary>
    internal sealed class RequestEventHandler : IExternalEventHandler, IRequestSink
    {
        private readonly ConcurrentQueue<PendingRequest> _queue = new ConcurrentQueue<PendingRequest>();
        private readonly CommandDispatcher _dispatcher;
        private readonly HostHealth _health;
        private ExternalEvent _event;

        public RequestEventHandler(CommandDispatcher dispatcher, HostHealth health)
        {
            _dispatcher = dispatcher;
            _health = health;
        }

        public void Attach(ExternalEvent externalEvent) => _event = externalEvent;

        public string GetName() => "B2M.RequestEventHandler";

        public JObject Health() => _health.Snapshot();

        public void Submit(PendingRequest request)
        {
            _queue.Enqueue(request);
            Raise();
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var request))
            {
                if (!request.TryStart()) continue; // Timed out before it started; nothing runs.
                request.Complete(Run(app, request));
                break;
            }
            if (!_queue.IsEmpty) Raise();
        }

        /// <summary>Fail every queued request that has not started. Called on shutdown.</summary>
        public void CancelAll()
        {
            while (_queue.TryDequeue(out var request))
                if (request.TryCancelBeforeStart())
                    request.Complete(Envelope.ErrorLine(request.Id, "REVIT_SHUTTING_DOWN: Revit is closing. Nothing ran."));
        }

        private void Raise()
        {
            var status = _event?.Raise();
            if (status == ExternalEventRequest.Denied || status == ExternalEventRequest.TimedOut)
                AddinLog.Write("Events", "ExternalEvent.Raise returned " + status);
        }

        private string Run(UIApplication app, PendingRequest request)
        {
            var sw = Stopwatch.StartNew();
            JObject target = null;
            FailureScope scope = null;
            var command = _dispatcher.Get(request.Command);
            JObject envelope;
            try
            {
                target = DocumentTargetGuard.Capture(app);
                _health.ObserveDocument(target.Value<string>("document_id") != null, target.Value<string>("document_title"));

                if (command == null)
                {
                    envelope = Envelope.Error(request.Id, "UNKNOWN_COMMAND: " + request.Command + ".",
                        new JObject { ["valid_commands"] = new JArray(_dispatcher.Names) });
                }
                else
                {
                    var validation = SchemaValidator.Validate(command.ParametersSchema, request.ParamsJson);
                    var rejection = !validation.IsValid ? null
                        : command.ReadOnly ? null
                        : DocumentTargetGuard.WriteRejection(request.ExpectedDocumentId, target.Value<string>("document_id"));
                    if (!validation.IsValid)
                    {
                        envelope = Envelope.Error(request.Id, validation.Error);
                        if (validation.Suggestion != null) envelope["suggestion"] = validation.Suggestion;
                    }
                    else if (rejection != null)
                    {
                        envelope = Envelope.Error(request.Id, rejection);
                    }
                    else
                    {
                        CommandResult result;
                        using (scope = FailureScope.Begin())
                            result = command.Execute(app, JObject.Parse(request.ParamsJson));
                        envelope = result.Success
                            ? Envelope.Ok(request.Id, result.Data)
                            : Envelope.Error(request.Id, ErrorSanitizer.Sanitize(result.Error), result.Data);
                    }
                }
            }
            catch (Exception ex)
            {
                envelope = Envelope.Error(request.Id, "COMMAND_FAILED: " + ErrorSanitizer.Sanitize(ex.Message));
            }

            if (target != null) envelope["_target"] = target;
            if (scope != null && scope.HasMessages)
            {
                envelope["failures"] = scope.ToJson();
                envelope["rolled_back_by_failure"] = scope.RolledBack;
            }

            var line = envelope.ToString(Formatting.None);
            if (line.Length > Envelope.MaxResponseChars) line = TooLarge(envelope, command);

            var success = envelope.Value<bool>("success");
            AddinLog.Write("Command", request.Command + " id=" + request.Id + " ok=" + success + " " + sw.ElapsedMilliseconds + " ms"
                + (success ? "" : " " + envelope.Value<string>("error"))
                + (scope != null && scope.HasMessages ? " failures=" + envelope["failures"].ToString(Formatting.None) : ""));
            return line;
        }

        private static string TooLarge(JObject envelope, IRevitCommand command)
        {
            var changed = envelope.Value<bool>("success") && command != null && !command.ReadOnly;
            var replacement = Envelope.Error(envelope.Value<string>("id"),
                "RESPONSE_TOO_LARGE: the result is over 1 MiB and was dropped. "
                + (changed ? "The command DID change the model. " : "")
                + "Ask for less, for example one level at a time.");
            replacement["_target"] = envelope["_target"];
            replacement["model_changed"] = changed;
            return replacement.ToString(Formatting.None);
        }
    }
}
