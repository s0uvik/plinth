// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Transport/PipeTransportServer.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: per-connection logic moved to ConnectionSession; discovery via DiscoveryPublisher;
// on .NET 8+ the pipe is created with CurrentUserOnly so only the same Windows user can connect.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Plinth.Revit.Core.Security;

namespace Plinth.Revit.Core.Transport
{
    /// <summary>Named-pipe server for Revit 2025 and later. One client at a time.</summary>
    public sealed class PipeTransportServer : ITransportServer
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(SafeHandle handle, IntPtr overlapped);

        private readonly DiscoveryPublisher _publisher;
        private readonly Action<string> _log;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly object _clientGate = new object();
        private readonly object _lifetimeGate = new object();
        private readonly string _pipeName;
        private bool _started, _stopped;
        private volatile bool _running;
        private Thread _listenThread;
        private IRequestSink _sink;
        private SessionToken _token;
        private NamedPipeServerStream _activeClient;

        public PipeTransportServer(DiscoveryPublisher publisher, Action<string> log, string pipeName = null)
        {
            _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
            _log = log ?? (_ => { });
            using (var process = Process.GetCurrentProcess())
                _pipeName = pipeName ?? "Plinth.Revit." + process.Id;
        }

        public string Kind => "pipe";
        public string PipeName => _pipeName;
        public bool IsRunning => _running;

        public void Start(IRequestSink sink)
        {
            lock (_lifetimeGate)
            {
                if (_started) throw new InvalidOperationException("A transport has one lifetime; create a new one to restart.");
                _started = true;
                _sink = sink ?? throw new ArgumentNullException(nameof(sink));
                try
                {
                    _activeClient = CreatePipe(_pipeName); // Reserve the name before advertising it.
                    _token = SessionToken.Generate();
                    _publisher.Publish(Kind, _pipeName, null, _token);
                    _running = true;
                    _listenThread = new Thread(ListenLoop) { IsBackground = true, Name = "Plinth.PipeTransportServer" };
                    _listenThread.Start();
                    _log("Listening on pipe " + _pipeName);
                }
                catch
                {
                    _running = false;
                    _stopped = true;
                    try { _activeClient?.Dispose(); } catch { }
                    try { _publisher.Dispose(); } catch { }
                    throw;
                }
            }
        }

        public void Stop()
        {
            lock (_lifetimeGate)
            {
                if (_stopped) return;
                _stopped = true;
                _running = false;
                _stop.Set();
                _cancel.Cancel();
                // CancelIoEx first: Dispose blocks while a synchronous read is pending on the pipe.
                lock (_clientGate)
                {
                    try { if (_activeClient != null) CancelIoEx(_activeClient.SafePipeHandle, IntPtr.Zero); } catch { }
                    try { _activeClient?.Dispose(); } catch { }
                }
                if (_listenThread != null && Thread.CurrentThread != _listenThread) _listenThread.Join(2000);
                try { _publisher.Dispose(); } catch { }
                _log("Stopped");
            }
        }

        public void Dispose() => Stop();

        private void ListenLoop()
        {
            var backoffMs = 100;
            var pipe = _activeClient;
            try
            {
                while (_running)
                {
                    try
                    {
                        pipe.WaitForConnectionAsync(_cancel.Token).GetAwaiter().GetResult();
                        if (!_running) break;
                        if (HandleClient(pipe)) backoffMs = 100;
                    }
                    catch (Exception) when (!_running)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _log("Listener failed: " + ex.Message);
                        try { pipe.Dispose(); } catch { }
                        if (_stop.WaitOne(backoffMs)) break;
                        backoffMs = Math.Min(backoffMs * 2, 2000);
                        pipe = CreatePipe(_pipeName);
                        lock (_clientGate) { _activeClient = pipe; }
                    }
                    finally
                    {
                        try { if (pipe.IsConnected) pipe.Disconnect(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                _log("Listener stopped after failure: " + ex.Message);
            }
            finally
            {
                _running = false;
                try { pipe?.Dispose(); } catch { }
            }
        }

        // Asynchronous (overlapped) so the writer thread can write while the listener thread reads.
        private static NamedPipeServerStream CreatePipe(string pipeName)
        {
            var options = PipeOptions.Asynchronous;
#if NET5_0_OR_GREATER
            options |= PipeOptions.CurrentUserOnly;
#endif
            return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, options);
        }

        private bool HandleClient(NamedPipeServerStream pipe)
        {
            lock (_clientGate) { _activeClient = pipe; }
            using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true))
            using (var writer = new TransportResponseWriter(
                new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true },
                onWriteFailure: () =>
                {
                    try { CancelIoEx(pipe.SafePipeHandle, IntPtr.Zero); } catch { }
                    try { pipe.Dispose(); } catch { }
                },
                // Disconnect discards unread bytes; let the client read the last line first.
                onDrained: () => { if (_running && pipe.IsConnected) pipe.WaitForPipeDrain(); },
                log: _log))
            using (new Timer(_ =>
            {
                if (!writer.IsIdleFor(ConnectionSession.IdleLimit)) return;
                _log("Closing idle client");
                try { CancelIoEx(pipe.SafePipeHandle, IntPtr.Zero); } catch { }
                try { pipe.Disconnect(); } catch { }
            }, null, ConnectionSession.IdleCheckPeriod, ConnectionSession.IdleCheckPeriod))
            {
                var authenticated = ConnectionSession.Run(reader, writer, _token, _sink, () => _running && pipe.IsConnected, _log);
                if (authenticated) _log("Client disconnected");
                return authenticated;
            }
        }
    }
}
