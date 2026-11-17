// New in Plinth.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Plinth.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Meta
{
    /// <summary>
    /// The per-machine switch for commands that run arbitrary C# (send_code_to_revit, run_baked_tool),
    /// and their audit log. Developer mode is off unless %LOCALAPPDATA%\Plinth\addin-settings.json holds
    /// {"developer_mode": true}. The file is read on every call, so turning it on or off needs no restart.
    /// Anything other than a literal true (missing file, bad JSON, "true" as a string) means off.
    /// </summary>
    internal static class DeveloperMode
    {
        private static readonly object Gate = new object();

        public static string SettingsPath => Path.Combine(PlinthPaths.Root, "addin-settings.json");

        public static string CodeLogPath => Path.Combine(PlinthPaths.Logs, "code-execution.jsonl");

        public static bool IsOn()
        {
            try
            {
                var path = SettingsPath;
                if (!File.Exists(path)) return false;
                var settings = JObject.Parse(File.ReadAllText(path));
                var flag = settings["developer_mode"];
                return flag != null && flag.Type == JTokenType.Boolean && flag.Value<bool>();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The refusal returned when developer mode is off. Names no absolute path.</summary>
        public static string OffError(string command) =>
            "DEVELOPER_MODE_OFF: " + command + " runs arbitrary C# inside Revit, so it is off on this machine. "
            + "A BIM manager can turn developer mode on by creating %LOCALAPPDATA%\\Plinth\\addin-settings.json "
            + "containing {\"developer_mode\": true} (it is read on every call; no Revit restart needed). "
            + "Every call, allowed or refused, is recorded in %LOCALAPPDATA%\\Plinth\\logs\\code-execution.jsonl.";

        /// <summary>
        /// Appends one record to the code-execution log. Returns false if the record could not be written;
        /// callers refuse to run code they cannot record.
        /// </summary>
        public static bool Log(string revitYear, string runId, string command, string tool, string code, string outcome, string error = null)
        {
            try
            {
                var record = new JObject
                {
                    ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                    ["revit_year"] = revitYear,
                    ["pid"] = CurrentProcessId(),
                    ["run_id"] = runId,
                    ["command"] = command,
                    ["outcome"] = outcome
                };
                if (tool != null) record["tool"] = tool;
                if (code != null) record["code"] = code;
                if (error != null) record["error"] = error;
                lock (Gate)
                {
                    Directory.CreateDirectory(PlinthPaths.Logs);
                    File.AppendAllText(CodeLogPath, record.ToString(Formatting.None) + Environment.NewLine, new UTF8Encoding(false));
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int CurrentProcessId()
        {
            using (var process = Process.GetCurrentProcess()) return process.Id;
        }
    }
}
