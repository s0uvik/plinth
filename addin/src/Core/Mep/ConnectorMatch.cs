// New in B2M (Step 10, F5): a fixture often has several connectors of one domain (a basin's cold water, hot water and
// waste), so "the nearest open piping connector" can pick the wrong one. create_mep_run first keeps the connectors whose
// classification fits the run's system: Revit's connector classification (PipeSystemType / DuctSystemType names) must
// equal the run system type's classification (MEPSystemClassification name), unless the connector takes any system
// (Global, Fitting, undefined). Names are compared, since the three Revit enums share them for the same system.

using System;

namespace B2M.Revit.Core.Mep
{
    public static class ConnectorMatch
    {
        public const int Exact = 0;
        public const int Any = 1;
        public const int Incompatible = -1;

        /// <summary>A connector classification that accepts every system of its domain.</summary>
        public static bool TakesAnySystem(string connectorClass) =>
            string.IsNullOrEmpty(connectorClass)
            || string.Equals(connectorClass, "Global", StringComparison.OrdinalIgnoreCase)
            || string.Equals(connectorClass, "Fitting", StringComparison.OrdinalIgnoreCase)
            || connectorClass.StartsWith("Undefined", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Exact when the names match, Any when the connector takes any system (or the run has no classification, as a
        /// conduit or cable tray run), Incompatible otherwise. Lower is better.
        /// </summary>
        public static int Rank(string connectorClass, string runClass)
        {
            if (string.IsNullOrEmpty(runClass)) return Any;
            if (string.Equals(connectorClass, runClass, StringComparison.OrdinalIgnoreCase)) return Exact;
            return TakesAnySystem(connectorClass) ? Any : Incompatible;
        }
    }
}
