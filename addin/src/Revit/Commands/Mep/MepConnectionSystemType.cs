// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Mep/MepConnectionSystemType.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: namespace only.

using System;
using Autodesk.Revit.DB;

namespace Plinth.Revit.Commands.Mep
{
    internal static class MepConnectionSystemType
    {
        /// <summary>
        /// The system type of the selected port, never an arbitrary system on multi-port equipment.
        /// Null means genuinely unassigned or not applicable; API failures propagate.
        /// </summary>
        public static MEPSystemType Read(Connector connector)
        {
            var domain = connector.Domain;
            if (domain != Domain.DomainPiping && domain != Domain.DomainHvac) return null;

            var system = connector.MEPSystem;
            ElementId typeId;
            if (system != null)
            {
                typeId = system.GetTypeId();
            }
            else if (connector.Owner is MEPCurve)
            {
                var parameter = connector.Owner.get_Parameter(domain == Domain.DomainPiping
                    ? BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM
                    : BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM);
                if (parameter == null) throw new InvalidOperationException("cannot read the selected curve's system type.");
                if (!parameter.HasValue) return null;
                typeId = parameter.AsElementId();
            }
            else
            {
                // An unassigned equipment port has no system yet; other ports must not supply its type.
                return null;
            }

            if (typeId == null || typeId == ElementId.InvalidElementId)
            {
                if (system == null) return null;
                throw new InvalidOperationException("the selected connector's system has no readable type.");
            }
            var type = connector.Owner.Document.GetElement(typeId) as MEPSystemType;
            if (type == null) throw new InvalidOperationException("cannot resolve the selected connector's system type.");
            return type;
        }

        public static bool Mismatch(MEPSystemType first, MEPSystemType second) =>
            first != null && second != null && first.Id != second.Id;
    }
}
