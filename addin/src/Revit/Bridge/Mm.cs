using System;

namespace B2M.Revit.Bridge
{
    /// <summary>
    /// The add-in speaks millimetres on the wire; Revit's internal unit is the foot.
    /// Every conversion goes through here (porting standard: one unit system, converted in one place).
    /// </summary>
    internal static class Mm
    {
        private const double MmPerFoot = 304.8;

        public static double ToFeet(double mm) => mm / MmPerFoot;

        public static double FromFeet(double feet) => Math.Round(feet * MmPerFoot, 1);
    }
}
