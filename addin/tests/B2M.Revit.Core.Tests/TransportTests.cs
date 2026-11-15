using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using B2M.Revit.Core.Execution;
using B2M.Revit.Core.Health;
using B2M.Revit.Core.Security;
using B2M.Revit.Core.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace B2M.Revit.Core.Tests
{
    /// <summary>Stands in for Revit: decides when queued requests start and finish.</summary>
    internal sealed class FakeSink : IRequestSink
    {
        public readonly BlockingCollection<PendingRequest> Received = new BlockingCollection<PendingRequest>();
        public Action<PendingRequest> OnSubmit = r =>
        {
            if (r.TryStart()) r.Complete(Envelope.Line(Envelope.Ok(r.Id, new JObject { ["echo"] = r.Command, ["params"] = JToken.Parse(r.ParamsJson) })));
        };

        public readonly HostHealth Health_ = new HostHealth("0.1.0", "2026", "test", new[] { new CommandInfo("create_level", false) });

        public void Submit(PendingRequest request)
        {
            Received.Add(request);
            OnSubmit(request);
        }

        public JObject Health() => Health_.Snapshot();
    }

    /// <summary>A line-based client over any stream, with a read timeout so a broken server fails the test fast.</summary>
    internal sealed class LineClient : IDisposable
    {
        private readonly Stream _stream;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        public LineClient(Stream stream)
        {
            _stream = stream;
            _reader = new StreamReader(stream, new UTF8Encoding(false));
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }

        public void Send(object request) => _writer.WriteLine(JsonConvert.SerializeObject(request));

        public JObject Receive(int timeoutMs = 10000)
        {
            var task = Task.Run(() => _reader.ReadLine());
            if (!task.Wait(timeoutMs)) throw new TimeoutException("No response line within " + timeoutMs + " ms.");
            return task.Result == null ? null : JObject.Parse(task.Result);
        }

        public void Dispose() => _stream.Dispose();
    }

    public sealed class TransportTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "b2m-tests-" + Guid.NewGuid().ToString("N"));
        private readonly FakeSink _sink = new FakeSink();

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private (PipeTransportServer server, JObject discovery) StartPipe()
        {
            var publisher = new DiscoveryPublisher(_dir, "2026", "0.1.0");
            var server = new PipeTransportServer(publisher, _ => { }, "B2M.Test." + Guid.NewGuid().ToString("N"));
            server.Start(_sink);
            return (server, JObject.Parse(File.ReadAllText(publisher.FilePath)));
        }

        private static LineClient ConnectPipe(string name)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(5000);
            return new LineClient(pipe);
        }

        [Fact]
        public void DiscoveryFileDescribesThePipeAndIsRemovedOnStop()
        {
            var (server, discovery) = StartPipe();
            Assert.Equal("pipe", discovery.Value<string>("transport"));
            Assert.Equal(server.PipeName, discovery.Value<string>("pipe_name"));
            Assert.Equal(2026, discovery.Value<int>("revit_year"));
            Assert.False(string.IsNullOrEmpty(discovery.Value<string>("auth_token")));
            server.Stop();
            Assert.Empty(Directory.GetFiles(_dir, "revit-*.json"));
        }

        [Fact]
        public void HealthAnswersWithoutQueueing()
        {
            var (server, discovery) = StartPipe();
            using (server)
            using (var client = ConnectPipe(server.PipeName))
            {
                client.Send(new { id = "h1", token = discovery.Value<string>("auth_token"), command = "health" });
                var response = client.Receive();
                Assert.True(response.Value<bool>("success"));
                Assert.Equal("h1", response.Value<string>("id"));
                Assert.Equal(1, response["data"].Value<int>("protocol_version"));
                Assert.Equal("create_level", response["data"]["commands"][0].Value<string>("name"));
                Assert.Empty(_sink.Received);
            }
        }

        [Fact]
        public void WrongTokenIsRejectedAndConnectionDropped()
        {
            var (server, _) = StartPipe();
            using (server)
            using (var client = ConnectPipe(server.PipeName))
            {
                client.Send(new { id = "x", token = "nope", command = "health" });
                Assert.StartsWith("UNAUTHORIZED", client.Receive().Value<string>("error"));
                Assert.Null(client.Receive());
            }
        }

        [Fact]
        public void CommandsReachTheSinkWithParamsAndDocument()
        {
            var (server, discovery) = StartPipe();
            using (server)
            using (var client = ConnectPipe(server.PipeName))
            {
                client.Send(new { id = 7, token = discovery.Value<string>("auth_token"), command = "create_level",
                    @params = new { elevation = 3000 }, expected_document_id = "doc-1" });
                var response = client.Receive();
                Assert.Equal("7", response.Value<string>("id"));
                Assert.Equal(3000, response["data"]["params"].Value<int>("elevation"));
                Assert.True(_sink.Received.TryTake(out var request));
                Assert.Equal("doc-1", request.ExpectedDocumentId);
            }
        }

        [Fact]
        public void RequestNotStartedBeforeTimeoutNeverRuns()
        {
            _sink.OnSubmit = _ => { }; // Revit never picks it up.
            var (server, discovery) = StartPipe();
            using (server)
            using (var client = ConnectPipe(server.PipeName))
            {
                client.Send(new { id = "slow", token = discovery.Value<string>("auth_token"), command = "create_level", timeout_seconds = 1 });
                Assert.StartsWith("REVIT_REQUEST_NOT_STARTED", client.Receive().Value<string>("error"));
                Assert.True(_sink.Received.TryTake(out var request));
                Assert.False(request.TryStart());
            }
        }

        [Fact]
        public void RunningRequestGetsInterimThenFinalResult()
        {
            _sink.OnSubmit = r =>
            {
                Assert.True(r.TryStart());
                Task.Delay(1500).ContinueWith(_ => r.Complete(Envelope.Line(Envelope.Ok(r.Id, new JValue("done")))));
            };
            var (server, discovery) = StartPipe();
            using (server)
            using (var client = ConnectPipe(server.PipeName))
            {
                client.Send(new { id = "long", token = discovery.Value<string>("auth_token"), command = "create_level", timeout_seconds = 1 });
                var interim = client.Receive();
                Assert.True(interim.Value<bool>("pending"));
                Assert.StartsWith("REVIT_OPERATION_TIMED_OUT", interim.Value<string>("error"));
                var final = client.Receive();
                Assert.Equal("long", final.Value<string>("id"));
                Assert.Equal("done", final.Value<string>("data"));
            }
        }

        [Fact]
        public void MalformedLinesAndMissingCommandGetErrors()
        {
            var (server, discovery) = StartPipe();
            using (server)
            using (var client = ConnectPipe(server.PipeName))
            {
                client.Send("not json");
                Assert.StartsWith("INVALID_JSON", client.Receive().Value<string>("error"));
                client.Send(new { id = "m", token = discovery.Value<string>("auth_token") });
                Assert.StartsWith("MISSING_COMMAND", client.Receive().Value<string>("error"));
            }
        }

        [Fact]
        public void TcpServerBindsLoopbackAndServesTheSameProtocol()
        {
            var publisher = new DiscoveryPublisher(_dir, "2024", "0.1.0");
            using (var server = new TcpTransportServer(publisher, _ => { }))
            {
                server.Start(_sink);
                var discovery = JObject.Parse(File.ReadAllText(publisher.FilePath));
                Assert.Equal("tcp", discovery.Value<string>("transport"));
                var tcp = new TcpClient();
                tcp.Connect("127.0.0.1", discovery.Value<int>("port"));
                using (var client = new LineClient(tcp.GetStream()))
                {
                    client.Send(new { id = "t", token = discovery.Value<string>("auth_token"), command = "create_level", @params = new { elevation = 1 } });
                    Assert.True(client.Receive().Value<bool>("success"));
                }
            }
        }

        [Fact]
        public void StaleDiscoveryFilesOfDeadProcessesAreRemoved()
        {
            Directory.CreateDirectory(_dir);
            var stale = Path.Combine(_dir, "revit-2026-999999.json");
            File.WriteAllText(stale, JsonConvert.SerializeObject(new { pid = 999999, process_start_utc = "2020-01-01T00:00:00.000Z" }));
            var (server, _) = StartPipe();
            using (server) Assert.False(File.Exists(stale));
        }
    }
}
