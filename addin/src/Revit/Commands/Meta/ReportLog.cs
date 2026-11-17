// New in Plinth: replaces rvt-mcp's SQLite report store and WPF Reports window with one JSON-lines file,
// %LOCALAPPDATA%\Plinth\reports.jsonl, one report per line, newest last.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Plinth.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Meta
{
    internal static class ReportLog
    {
        private static readonly object Gate = new object();

        public static string FilePath => Path.Combine(PlinthPaths.Root, "reports.jsonl");

        /// <summary>Appends one record. Throws IOException or UnauthorizedAccessException if it cannot.</summary>
        public static void Append(JObject record)
        {
            lock (Gate)
            {
                Directory.CreateDirectory(PlinthPaths.Root);
                File.AppendAllText(FilePath, record.ToString(Formatting.None) + Environment.NewLine, new UTF8Encoding(false));
            }
        }

        /// <summary>The last <paramref name="count"/> readable records, newest first, plus how many lines were unreadable.</summary>
        public static List<JObject> ReadLast(int count, out int total, out int skipped)
        {
            var records = new List<JObject>();
            total = 0;
            skipped = 0;
            if (!File.Exists(FilePath)) return records;

            string[] lines;
            lock (Gate)
            {
                using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    lines = reader.ReadToEnd().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            }
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;
                JObject record;
                try
                {
                    using (var json = new JsonTextReader(new StringReader(line)) { DateParseHandling = DateParseHandling.None })
                        record = JToken.ReadFrom(json) as JObject;
                    if (record == null) throw new JsonReaderException("not an object");
                }
                catch (JsonException)
                {
                    skipped++;
                    continue;
                }
                total++;
                if (records.Count < count) records.Add(record);
            }
            return records;
        }
    }
}
