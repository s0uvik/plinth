// New in Plinth (2026-11-27, after a Revit API review of create_electrical_circuit): which circuit types the tool takes and
// which connectors may join which circuit, Revit-free so it is unit-tested. Names are Revit's ElectricalSystemType names.
// A circuit (ElectricalSystem.Create) is one of eight types; PowerBalanced, PowerUnBalanced and UndefinedSystemType are
// connector types, not circuit types, so they are refused as system_type with the list of those that are.
// A PowerCircuit takes the power connectors real families carry: the family editor offers only Power - Balanced and
// Power - Unbalanced for a power connector, so checking for an exact PowerCircuit refused every real socket. Revit
// documents an unconnected connector's system type as possibly undefined (and reading it can throw), so such a connector
// is not refused here: ElectricalSystem.Create, and the membership check after it, decide.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Plinth.Revit.Core.Mep
{
    public static class CircuitTypes
    {
        public const string PowerCircuit = "PowerCircuit";

        /// <summary>The ElectricalSystemType names a circuit can be made as, in the order they are listed.</summary>
        public static readonly IReadOnlyList<string> Valid = new[]
        {
            PowerCircuit, "Data", "Communication", "Controls", "FireAlarm", "NurseCall", "Security", "Telephone"
        };

        /// <summary>The connector types a PowerCircuit takes.</summary>
        public static readonly IReadOnlyList<string> PowerConnectorTypes = new[] { "PowerBalanced", "PowerUnBalanced", PowerCircuit };

        /// <summary>The connector joins a circuit of the type.</summary>
        public const int Joins = 0;

        /// <summary>The connector's type is undefined or unreadable: let Revit decide when it makes the circuit.</summary>
        public const int RevitDecides = 1;

        /// <summary>The connector is of another type and cannot join.</summary>
        public const int Refused = -1;

        /// <summary>
        /// Why system_type cannot be a circuit type, or null; circuitType gets Revit's spelling (matched case-insensitively,
        /// PowerCircuit when given is null).
        /// </summary>
        public static string Error(string given, out string circuitType)
        {
            var name = given == null ? PowerCircuit : given.Trim();
            circuitType = Valid.FirstOrDefault(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase));
            return circuitType != null ? null
                : "system_type '" + given + "' is not a circuit type; use one of " + string.Join(", ", Valid);
        }

        /// <summary>
        /// Whether a connector of connectorType (an ElectricalSystemType name; null when it could not be read) can join a
        /// circuit of circuitType: Joins, RevitDecides (undefined or unreadable) or Refused.
        /// </summary>
        public static int Fit(string circuitType, string connectorType)
        {
            if (string.IsNullOrEmpty(connectorType) || connectorType.StartsWith("Undefined", StringComparison.OrdinalIgnoreCase))
                return RevitDecides;
            if (string.Equals(circuitType, PowerCircuit, StringComparison.OrdinalIgnoreCase))
                return PowerConnectorTypes.Any(t => string.Equals(t, connectorType, StringComparison.OrdinalIgnoreCase)) ? Joins : Refused;
            return string.Equals(circuitType, connectorType, StringComparison.OrdinalIgnoreCase) ? Joins : Refused;
        }
    }
}
