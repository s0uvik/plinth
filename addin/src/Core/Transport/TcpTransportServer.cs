// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Transport/TcpTransportServer.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: per-connection logic moved to ConnectionSession; discovery via DiscoveryPublisher;
// the LAN-bind opt-in is removed, so the listener always binds 127.0.0.1.

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using B2M.Revit.Core.Security;

namespace B2M.Revit.Core.Transport
{
    /// <summary>Loopback TCP server used by the Revit 2024 build. One client at a time.</summary>
    public sealed class TcpTransportServer : ITransportServer
    {
        private readonly DiscoveryPublisher _publisher;
        private readonly Action<string> _log;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly object _clientGate = new object();
        private readonly object _lifetimeGate = new object();
        private bool _started, _stopped;
        private volatile bool _running;
        private TcpListener _listener;
        private Thread _listenThread;
        private IRequestSink _sink;
        private SessionToken _token;
        private TcpClient _activeClient;

        public TcpTransportServer(DiscoveryPublisher publisher, Action<string> log)
        {
            _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
            _log = log ?? (_ => { });
        }

        public string Kind => "tcp";
        public int Port { get; private set; }
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
                    _listener = new TcpListener(IPAddress.Loopback, 0);
                    _listener.Start();
                    Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                    _token = SessionToken.Generate();
                    _publisher.Publish(Kind, null, Port, _token);
                    _running = true;
                    _listenThread = new Thread(ListenLoop) { IsBackground = true, Name = "B2M.TcpTransportServer" };
                    _listenThread.Start();
                    _log("Listening on 127.0.0.1:" + Port);
                }
                catch
                {
                    _running = false;
                    _stopped = true;
                    try { _listener?.Stop(); } catch { }
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
                lock (_clientGate) { try { _activeClient?.Close(); } catch { } }
                try { _listener?.Stop(); } catch { }
                if (_listenThread != null && Thread.CurrentThread != _listenThread) _listenThread.Join(2000);
                try { _publisher.Dispose(); } catch { }
                _log("Stopped");
            }
        }

        public void Dispose() => Stop();

        private void ListenLoop()
        {
            var backoffMs = 100;
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                    if (HandleClient(client)) backoffMs = 100;
                }
                catch (Exception) when (!_running)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log("Listener failed: " + ex.Message);
                    if (_stop.WaitOne(backoffMs)) break;
                    backoffMs = Math.Min(backoffMs * 2, 2000);
                }
                finally
                {
                    try { client?.Close(); } catch { }
                }
            }
        }

        private bool HandleClient(TcpClient client)
        {
            // No socket read timeout: it would also fire while a long request runs. Idle release is below.
            client.ReceiveTimeout = 0;
            lock (_clientGate) { _activeClient = client; }
            var stream = client.GetStream();
            using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true))
            using (var writer = new TransportResponseWriter(
                new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true },
                onWriteFailure: () => { try { client.Close(); } catch { } },
                onDrained: null,
                log: _log))
            using (new Timer(_ =>
            {
                if (!writer.IsIdleFor(ConnectionSession.IdleLimit)) return;
                _log("Closing idle client");
                try { client.Close(); } catch { }
            }, null, ConnectionSession.IdleCheckPeriod, ConnectionSession.IdleCheckPeriod))
            {
                var authenticated = ConnectionSession.Run(reader, writer, _token, _sink, () => _running && client.Connected, _log);
                if (authenticated) _log("Client disconnected");
                return authenticated;
            }
        }
    }
}
