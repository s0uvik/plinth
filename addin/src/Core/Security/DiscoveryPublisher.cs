// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// files src/shared/Security/DiscoveryPublisher.cs and src/shared/Security/AuthToken.cs, commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for B2M: one discovery file per Revit process only (the shared per-year "legacy"
// file, its handover logic and the cross-process mutex are gone); files live under
// %LOCALAPPDATA%\B2M\discovery; stale files of dead processes are removed on publish;
// the file records the add-in version.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Core.Security
{
    /// <summary>
    /// Writes the discovery file a client reads to find this Revit process: transport,
    /// pipe name or port, and the session token. One file per process, owner-only ACL.
    /// </summary>
    public sealed class DiscoveryPublisher : IDisposable
    {
        public const int SchemaVersion = 1;

        private readonly object _gate = new object();
        private readonly string _directory;
        private readonly string _year;
        private readonly string _addinVersion;
        private readonly int _pid;
        private readonly DateTime _startUtc;
        private readonly Action<string> _log;
        private bool _disposed;
        private string _token;

        public DiscoveryPublisher(string directory, string revitYear, string addinVersion, Action<string> log = null)
        {
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            _year = revitYear ?? "unknown";
            _addinVersion = addinVersion ?? "0.0.0";
            _log = log;
            using (var process = Process.GetCurrentProcess())
            {
                _pid = process.Id;
                _startUtc = process.StartTime.ToUniversalTime();
            }
        }

        public string TargetId => "revit-" + _year + "-" + _pid.ToString(CultureInfo.InvariantCulture);

        public string FilePath => Path.Combine(_directory, TargetId + ".json");

        public void Publish(string transport, string pipeName, int? port, SessionToken token)
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(DiscoveryPublisher));
                RemoveStaleFiles();
                _token = token.Value;
                var json = new JObject
                {
                    ["schema_version"] = SchemaVersion,
                    ["product"] = "b2m-revit",
                    ["addin_version"] = _addinVersion,
                    ["revit_year"] = int.TryParse(_year, out var y) ? new JValue(y) : JValue.CreateNull(),
                    ["pid"] = _pid,
                    ["process_start_utc"] = _startUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                    ["target_id"] = TargetId,
                    ["transport"] = transport,
                    ["pipe_name"] = pipeName == null ? JValue.CreateNull() : new JValue(pipeName),
                    ["port"] = port.HasValue ? new JValue(port.Value) : JValue.CreateNull(),
                    ["auth_token"] = token.Value
                }.ToString(Formatting.Indented);
                if (!AtomicFile.TryWrite(FilePath, json, _pid, RestrictToCurrentUser, _log))
                    throw new IOException("The discovery file could not be written.");
                Log("Published " + Path.GetFileName(FilePath));
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                // A late Stop of an old listener must not delete a newer listener's file.
                var own = TryRead(FilePath);
                if (own != null && own.Value<string>("auth_token") == _token) TryDelete(FilePath);
            }
        }

        private void RemoveStaleFiles()
        {
            try
            {
                if (!Directory.Exists(_directory)) return;
                foreach (var path in Directory.GetFiles(_directory, "revit-*.json"))
                {
                    var file = TryRead(path);
                    var pid = file?.Value<int?>("pid") ?? 0;
                    if (pid == _pid) continue;
                    DateTime? start = null;
                    if (DateTime.TryParse(file?.Value<string>("process_start_utc"), CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                        start = parsed;
                    if (file == null || pid <= 0 || !IsProcessAlive(pid, start))
                    {
                        TryDelete(path);
                        Log("Removed stale discovery file " + Path.GetFileName(path));
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Stale discovery scan failed: " + ex.Message);
            }
        }

        internal static bool IsProcessAlive(int pid, DateTime? expectedStartUtc)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    if (process.HasExited) return false;
                    if (!expectedStartUtc.HasValue) return true;
                    return Math.Abs((process.StartTime.ToUniversalTime() - expectedStartUtc.Value).TotalSeconds) <= 1;
                }
            }
            catch (ArgumentException) { return false; }
            catch { return true; } // Lack of permission is not proof of death.
        }

        private static void RestrictToCurrentUser(string path)
        {
            var file = new FileInfo(path);
            var acl = file.GetAccessControl();
            acl.SetAccessRuleProtection(true, false);
            var sid = WindowsIdentity.GetCurrent().User;
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            file.SetAccessControl(acl);
        }

        private static JObject TryRead(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream)) return JObject.Parse(reader.ReadToEnd());
            }
            catch { return null; }
        }

        private void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) { Log("Discovery cleanup failed: " + ex.Message); }
        }

        private void Log(string message)
        {
            try { _log?.Invoke(message); } catch { }
        }
    }

    /// <summary>Write to a temp file, then replace, so a reader never sees a half-written file.</summary>
    public static class AtomicFile
    {
        public static bool TryWrite(string path, string content, int pid, Action<string> restrictAcl = null, Action<string> log = null)
        {
            var temp = path + "." + pid.ToString(CultureInfo.InvariantCulture) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(content);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                try { restrictAcl?.Invoke(temp); }
                catch (Exception ex) { SafeLog(log, "ACL restriction failed: " + ex.Message); }
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temp, path, null);
                        else File.Move(temp, path);
                        return true;
                    }
                    catch (IOException) when (attempt < 5) { Thread.Sleep(50 << attempt); }
                    catch (UnauthorizedAccessException) when (attempt < 5) { Thread.Sleep(50 << attempt); }
                }
            }
            catch (Exception ex)
            {
                SafeLog(log, "Discovery write failed: " + ex.Message);
                return false;
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        }

        private static void SafeLog(Action<string> log, string message)
        {
            try { log?.Invoke(message); } catch { }
        }
    }
}
