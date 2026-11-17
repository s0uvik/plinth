// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// files src/shared/Handlers/Mep/MepSystemMembership.cs and src/shared/Handlers/Mep/MepMembershipPolicy.cs, commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for Plinth: the two files merged; the counts policy is a nested struct; namespace and ID helper renamed.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Plinth.Revit.Bridge;

namespace Plinth.Revit.Commands.Mep
{
    /// <summary>
    /// Reads each API collection of an MEP system once. A failed read must never look like an empty system.
    /// Piping and HVAC keep the flow network (pipes, ducts, fittings) apart from terminals; electrical has one list.
    /// AllElements excludes nothing: flow, terminals and base equipment, deduplicated by ID.
    /// </summary>
    internal sealed class MepSystemMembership
    {
        public readonly struct MembershipCounts
        {
            public MembershipCounts(int elementCount, int terminalCount, bool isEmpty)
            {
                ElementCount = elementCount;
                TerminalCount = terminalCount;
                IsEmpty = isEmpty;
            }

            public int ElementCount { get; }
            public int TerminalCount { get; }
            public bool IsEmpty { get; }

            public static MembershipCounts Evaluate(bool separateFlowNetwork, int flowCount, int terminalCount, bool hasBaseEquipment)
            {
                if (!separateFlowNetwork)
                    return new MembershipCounts(terminalCount, terminalCount, terminalCount == 0 && !hasBaseEquipment);
                return new MembershipCounts(flowCount, terminalCount, flowCount == 0 && terminalCount == 0 && !hasBaseEquipment);
            }
        }

        private MepSystemMembership(Element[] flow, Element[] terminals, FamilyInstance equipment, bool separateFlowNetwork)
        {
            FlowElements = flow;
            BaseEquipment = equipment;
            Counts = MembershipCounts.Evaluate(separateFlowNetwork, flow.Length, terminals.Length, equipment != null);
            AllElements = flow.Concat(terminals)
                .Concat(equipment == null ? new Element[0] : new Element[] { equipment })
                .GroupBy(element => RevitCompat.GetId(element.Id))
                .Select(group => group.First()).ToArray();
        }

        public Element[] FlowElements { get; }
        public Element[] AllElements { get; }
        public FamilyInstance BaseEquipment { get; }
        public MembershipCounts Counts { get; }

        public static MepSystemMembership Read(MEPSystem system)
        {
            try
            {
                var terminals = ReadElements(system.Elements, "Elements");
                var flow = terminals;
                var separate = false;
                if (system is PipingSystem piping)
                {
                    flow = ReadElements(piping.PipingNetwork, "PipingNetwork");
                    separate = true;
                }
                else if (system is MechanicalSystem mechanical)
                {
                    flow = ReadElements(mechanical.DuctNetwork, "DuctNetwork");
                    separate = true;
                }
                return new MepSystemMembership(flow, terminals, system.BaseEquipment, separate);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("could not read the MEP system's members: " + ex.Message, ex);
            }
        }

        private static Element[] ReadElements(ElementSet elements, string source)
        {
            if (elements == null) throw new InvalidOperationException(source + " returned no collection.");
            var members = elements.Cast<Element>().ToArray();
            if (members.Any(element => element == null)) throw new InvalidOperationException(source + " contains an unreadable member.");
            return members;
        }
    }
}
