// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// files src/shared/Transport/PipeTransportServer.cs, TcpTransportServer.cs and TransportResponseWriter.cs,
// commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: the per-connection loop both transports duplicated (bounded line reads, token
// check, rate limit, health, dispatch with timeout) now lives here once; the tool-catalog handshake
// is gone; the rate limit is raised from 20 to 200 requests per 10 s so a full build plan plus
// read-back fits; malformed JSON gets an error reply instead of silence.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using B2M.Revit.Core.Execution;
using B2M.Revit.Core.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Core.Transport
{
    /// <summary>
    /// Wire protocol: one JSON object per line in each direction.
    /// Request:  {"id","token","command","params"?,"expected_document_id"?,"timeout_seconds"?}
    /// Response: {"id","success","data"?,"error"?,...}. A request still running at its timeout gets an
    /// interim {"pending":true} line; its final line follows later with the same id.
    /// </summary>
    internal static class ConnectionSession
    {
        internal const int MaxLineChars = 1024 * 1024;
        internal const int RateLimitMax = 200;
        internal static readonly TimeSpan RateLimitWindow = TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(120);
        internal static readonly TimeSpan IdleCheckPeriod = TimeSpan.FromSeconds(15);

        /// <summary>Serve one connected client until it disconnects or the server stops.</summary>
        /// <returns>True if the client authenticated at least once.</returns>
        internal static bool Run(TextReader reader, TransportResponseWriter writer, SessionToken token,
            IRequestSink sink, Func<bool> isRunning, Action<string> log)
        {
            var authenticated = false;
            var stamps = new Queue<DateTime>();
            while (isRunning())
            {
                string line;
                try
                {
                    line = ReadLineBounded(reader, MaxLineChars, out var overflow);
                    if (overflow)
                    {
                        log("Dropped request over 1 MiB");
                        writer.WriteLine(Envelope.ErrorLine(null, "REQUEST_TOO_LARGE: requests are limited to 1 MiB."));
                        break;
                    }
                    if (line == null) break; // Client disconnected.
                }
                catch (IOException) { break; }
                catch (ObjectDisposedException) { break; }

                if (string.IsNullOrWhiteSpace(line)) continue;

                JObject request;
                try { request = JObject.Parse(line); }
                catch (JsonException)
                {
                    writer.WriteLine(Envelope.ErrorLine(null, "INVALID_JSON: each request must be one JSON object on one line."));
                    continue;
                }

                var id = AsString(request["id"]);
                if (!token.Verify(AsString(request["token"])))
                {
                    log("Rejected a request with a missing or wrong token");
                    writer.WriteLine(Envelope.ErrorLine(id, "UNAUTHORIZED: missing or wrong token. Read it from the discovery file."));
                    break; // Drop the connection on auth failure.
                }
                if (!authenticated)
                {
                    authenticated = true;
                    log("Client connected");
                }
                writer.Touch();

                var now = DateTime.UtcNow;
                while (stamps.Count > 0 && now - stamps.Peek() > RateLimitWindow) stamps.Dequeue();
                if (stamps.Count >= RateLimitMax)
                {
                    log("Rate limit exceeded; dropping connection");
                    writer.WriteLine(Envelope.ErrorLine(id, "RATE_LIMITED: at most 200 requests per 10 seconds per connection."));
                    break;
                }
                stamps.Enqueue(now);

                var command = AsString(request["command"]);
                if (string.IsNullOrEmpty(command))
                {
                    writer.WriteLine(Envelope.ErrorLine(id, "MISSING_COMMAND: the request has no 'command'."));
                    continue;
                }

                // Health never enters Revit's queue, so it answers even while Revit is busy.
                if (string.Equals(command, "health", StringComparison.Ordinal))
                {
                    JObject health;
                    try { health = sink.Health(); }
                    catch (Exception ex) { health = new JObject { ["error"] = ErrorSanitizer.Sanitize(ex.Message) }; }
                    writer.WriteLine(Envelope.Line(Envelope.Ok(id, health)));
                    continue;
                }

                Dispatch(request, id, command, writer, sink, log);
            }
            return authenticated;
        }

        private static void Dispatch(JObject request, string id, string command, TransportResponseWriter writer,
            IRequestSink sink, Action<string> log)
        {
            var paramsToken = request["params"];
            var pending = new PendingRequest(id, command,
                paramsToken == null || paramsToken.Type == JTokenType.Null ? "{}" : paramsToken.ToString(Formatting.None),
                AsString(request["expected_document_id"]));
            int? seconds = null;
            var timeoutToken = request["timeout_seconds"];
            if (timeoutToken != null && timeoutToken.Type == JTokenType.Integer) seconds = timeoutToken.Value<int>();
            var timeout = RequestTimeouts.Effective(seconds);

            writer.BeginRequest();
            var timer = new Timer(_ =>
            {
                if (pending.TryCancelBeforeStart())
                {
                    pending.Complete(Envelope.ErrorLine(id,
                        "REVIT_REQUEST_NOT_STARTED: Revit was busy and did not start this request within "
                        + (int)timeout.TotalSeconds + " s. Nothing ran; it is safe to retry."));
                }
                else if (!pending.Completion.Task.IsCompleted)
                {
                    var interim = Envelope.Error(id,
                        "REVIT_OPERATION_TIMED_OUT: still running in Revit after " + (int)timeout.TotalSeconds
                        + " s. Its final result will follow on this connection with the same id. Do not retry.");
                    interim["pending"] = true;
                    writer.WriteLine(Envelope.Line(interim));
                }
            }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            pending.Completion.Task.ContinueWith(task =>
            {
                timer.Dispose();
                if (task.Status == TaskStatus.RanToCompletion) writer.WriteLine(task.Result);
                writer.EndRequest();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            timer.Change(timeout, Timeout.InfiniteTimeSpan);
            try
            {
                sink.Submit(pending);
            }
            catch (Exception ex)
            {
                log("Could not queue request: " + ex.Message);
                if (pending.TryCancelBeforeStart())
                    pending.Complete(Envelope.ErrorLine(id, "REQUEST_NOT_QUEUED: the add-in could not queue this request. Nothing ran."));
            }
        }

        private static string AsString(JToken token) =>
            token == null || token.Type == JTokenType.Null ? null
            : token.Type == JTokenType.String ? token.Value<string>()
            : token.ToString(Formatting.None);

        internal static string ReadLineBounded(TextReader reader, int maxChars, out bool overflow)
        {
            overflow = false;
            var sb = new StringBuilder();
            while (true)
            {
                var ch = reader.Read();
                if (ch == -1) return sb.Length == 0 ? null : sb.ToString();
                if (ch == '\n') return sb.ToString();
                if (ch == '\r') continue;
                if (sb.Length >= maxChars)
                {
                    overflow = true;
                    return null;
                }
                sb.Append((char)ch);
            }
        }
    }
}
