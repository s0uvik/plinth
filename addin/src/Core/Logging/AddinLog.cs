using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace B2M.Revit.Core.Logging
{
    /// <summary>
    /// The add-in's single log. One file per day in the shared B2M log folder; every line
    /// carries the Revit year and process ID so service and add-in logs can be read together.
    /// Logging never throws into the caller.
    /// </summary>
    public static class AddinLog
    {
        private static readonly object Gate = new object();
        private static readonly int ProcessId = CurrentProcessId();
        private static string _revitYear = "unknown";

        public static void Initialize(string revitYear)
        {
            if (!string.IsNullOrWhiteSpace(revitYear)) _revitYear = revitYear;
        }

        public static void Write(string category, string message)
        {
            try
            {
                var now = DateTime.UtcNow;
                var line = string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-ddTHH:mm:ss.fffZ} [{1} pid {2}] [{3}] {4}",
                    now, _revitYear, ProcessId, category, message);
                var path = Path.Combine(B2MPaths.Logs, "addin-" + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                lock (Gate)
                {
                    Directory.CreateDirectory(B2MPaths.Logs);
                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch
            {
                // A full disk or locked file must never break a Revit command.
            }
        }

        private static int CurrentProcessId()
        {
            using (var process = Process.GetCurrentProcess()) return process.Id;
        }
    }
}
