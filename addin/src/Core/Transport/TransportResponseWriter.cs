// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Transport/TransportResponseWriter.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: request dispatch moved to ConnectionSession; script-protocol, failure-protocol
// and host-busy checks removed; in-flight tracking exposed as Begin/EndRequest.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Plinth.Revit.Core.Transport
{
    /// <summary>
    /// One background thread owns the connection's write side. Revit's main thread and timer
    /// callbacks only enqueue finished lines, so a slow client can never block Revit.
    /// </summary>
    internal sealed class TransportResponseWriter : IDisposable
    {
        private const int MaxPendingFrames = 256;
        private const long MaxPendingBytes = 32L * 1024 * 1024;
        internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(1);

        private readonly BlockingCollection<string> _frames = new BlockingCollection<string>(MaxPendingFrames);
        private readonly object _queueGate = new object();
        private readonly StreamWriter _writer;
        private readonly Action _onWriteFailure;
        private readonly Action<string> _log;
        private readonly Thread _thread;
        private bool _closing;
        private int _pendingFrames;
        private long _pendingBytes;
        private int _inFlight;
        private long _lastActivityTicks = Stopwatch.GetTimestamp();

        internal TransportResponseWriter(StreamWriter writer, Action onWriteFailure, Action onDrained, Action<string> log)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _onWriteFailure = onWriteFailure;
            _log = log;
            _thread = new Thread(() =>
            {
                try
                {
                    foreach (var frame in _frames.GetConsumingEnumerable())
                    {
                        _writer.WriteLine(frame);
                        _writer.Flush();
                        Touch();
                        Interlocked.Decrement(ref _pendingFrames);
                        Interlocked.Add(ref _pendingBytes, -Encoding.UTF8.GetByteCount(frame));
                    }
                    onDrained?.Invoke();
                }
                catch
                {
                    CompleteAdding();
                    // Wake a reader parked on the failed connection so a new client can connect.
                    try { onWriteFailure?.Invoke(); } catch { }
                }
                finally
                {
                    try { _writer.Dispose(); } catch { }
                    lock (_queueGate) { _frames.Dispose(); }
                }
            })
            { IsBackground = true, Name = "Plinth.TransportWriter" };
            _thread.Start();
        }

        /// <summary>Requests on this connection whose result has not been written yet.</summary>
        internal int InFlight => Volatile.Read(ref _inFlight);

        internal void BeginRequest()
        {
            Interlocked.Increment(ref _inFlight);
            Touch();
        }

        internal void EndRequest()
        {
            Touch();
            Interlocked.Decrement(ref _inFlight);
        }

        internal void Touch() => Interlocked.Exchange(ref _lastActivityTicks, Stopwatch.GetTimestamp());

        /// <summary>True when nothing is in flight or queued and the connection has been quiet for <paramref name="limit"/>.</summary>
        internal bool IsIdleFor(TimeSpan limit) =>
            InFlight == 0 && Volatile.Read(ref _pendingFrames) == 0
            && (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastActivityTicks)) / (double)Stopwatch.Frequency >= limit.TotalSeconds;

        internal void WriteLine(string frame)
        {
            var overflow = false;
            lock (_queueGate)
            {
                if (_closing) return;
                var bytes = Encoding.UTF8.GetByteCount(frame);
                if (_pendingFrames >= MaxPendingFrames || _pendingBytes + bytes > MaxPendingBytes)
                {
                    _closing = true;
                    _frames.CompleteAdding();
                    overflow = true;
                }
                else
                {
                    Interlocked.Increment(ref _pendingFrames);
                    Interlocked.Add(ref _pendingBytes, bytes);
                    _frames.Add(frame);
                }
            }
            if (overflow)
            {
                try { _log?.Invoke("Client did not read its responses; connection closed."); } catch { }
                try { _onWriteFailure?.Invoke(); } catch { }
            }
        }

        private void CompleteAdding()
        {
            lock (_queueGate)
            {
                if (_closing) return;
                _closing = true;
                _frames.CompleteAdding();
            }
        }

        public void Dispose()
        {
            CompleteAdding();
            // Drain queued lines, not unfinished Revit work. If the client is not reading,
            // the transport closes its stream after this deadline to unblock the writer.
            if (Thread.CurrentThread != _thread && !_thread.Join(DrainTimeout))
            {
                try { _onWriteFailure?.Invoke(); } catch { }
                _thread.Join(DrainTimeout);
            }
        }
    }
}
