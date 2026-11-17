// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// files src/shared/Infrastructure/HostHealthSnapshot.cs and RuntimeIdentity.cs, commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for Plinth: one small snapshot with add-in version, Revit year, process ID, transport,
// protocol version and the command list the client checks at startup; script and update fields removed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Core.Health
{
    /// <summary>
    /// What <c>health</c> returns. Answered on a transport thread, so it never calls the Revit API;
    /// the document fields are the last observation made on Revit's main thread.
    /// </summary>
    public sealed class HostHealth
    {
        public const int ProtocolVersion = 1;

        private readonly object _gate = new object();
        private readonly JObject _identity;
        private readonly JArray _commands;
        private JObject _document;
        private string _observedUtc;

        public HostHealth(string addinVersion, string revitYear, string transport, IEnumerable<CommandInfo> commands)
        {
            using (var process = Process.GetCurrentProcess())
                _identity = new JObject
                {
                    ["product"] = "plinth-revit",
                    ["addin_version"] = addinVersion,
                    ["protocol_version"] = ProtocolVersion,
                    ["revit_year"] = int.TryParse(revitYear, out var y) ? new JValue(y) : JValue.CreateNull(),
                    ["process_id"] = process.Id,
                    ["transport"] = transport
                };
            _commands = new JArray();
            foreach (var c in commands)
                _commands.Add(new JObject { ["name"] = c.Name, ["read_only"] = c.ReadOnly });
        }

        /// <summary>Record what Revit's main thread last saw. Never keep Revit objects or file paths here.</summary>
        public void ObserveDocument(bool hasDocument, string title)
        {
            lock (_gate)
            {
                _document = new JObject { ["has_active_document"] = hasDocument, ["document_title"] = title };
                _observedUtc = DateTimeOffset.UtcNow.ToString("o");
            }
        }

        public JObject Snapshot()
        {
            lock (_gate)
            {
                var snapshot = (JObject)_identity.DeepClone();
                snapshot["document"] = _document?.DeepClone() ?? JValue.CreateNull();
                snapshot["document_observed_utc"] = _observedUtc;
                snapshot["commands"] = _commands.DeepClone();
                return snapshot;
            }
        }
    }

    public struct CommandInfo
    {
        public CommandInfo(string name, bool readOnly)
        {
            Name = name;
            ReadOnly = readOnly;
        }

        public string Name { get; }
        public bool ReadOnly { get; }
    }
}
