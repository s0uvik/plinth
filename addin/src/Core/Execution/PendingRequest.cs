// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// files src/shared/Infrastructure/Execution/PendingRequestLifecycle.cs and RequestWait.cs, commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for B2M: one small request object replaces the lifecycle table; script, dialog-wait,
// modal-host and operation-receipt states are gone. The queued/running/cancelled states and the
// 60 s default / 900 s maximum wait are kept.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace B2M.Revit.Core.Execution
{
    /// <summary>
    /// One request travelling from a transport thread to Revit's main thread. A request that
    /// times out before it starts is cancelled and never runs; one that already started
    /// always finishes and delivers its result.
    /// </summary>
    public sealed class PendingRequest
    {
        private const int Queued = 0, Running = 1, Cancelled = 2;
        private int _state;

        public PendingRequest(string id, string command, string paramsJson, string expectedDocumentId)
        {
            Id = id;
            Command = command;
            ParamsJson = string.IsNullOrWhiteSpace(paramsJson) ? "{}" : paramsJson;
            ExpectedDocumentId = string.IsNullOrWhiteSpace(expectedDocumentId) ? null : expectedDocumentId;
        }

        public string Id { get; }
        public string Command { get; }
        public string ParamsJson { get; }
        public string ExpectedDocumentId { get; }

        /// <summary>Completed with the full JSON response line.</summary>
        public TaskCompletionSource<string> Completion { get; } =
            new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasStarted => Volatile.Read(ref _state) == Running;

        /// <summary>Called on Revit's main thread; false means the request was cancelled and must be skipped.</summary>
        public bool TryStart() => Interlocked.CompareExchange(ref _state, Running, Queued) == Queued;

        /// <summary>Called on timeout; true only if the request had not started, so nothing ran.</summary>
        public bool TryCancelBeforeStart() => Interlocked.CompareExchange(ref _state, Cancelled, Queued) == Queued;

        public void Complete(string responseJson) => Completion.TrySetResult(responseJson);
    }

    public static class RequestTimeouts
    {
        public static readonly TimeSpan Default = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan Max = TimeSpan.FromSeconds(900);

        /// <summary>The caller's wait when usable, 60 s when absent or not positive, never more than 900 s.</summary>
        public static TimeSpan Effective(int? seconds)
        {
            if (!seconds.HasValue || seconds.Value <= 0) return Default;
            var wait = TimeSpan.FromSeconds(seconds.Value);
            return wait > Max ? Max : wait;
        }
    }
}
