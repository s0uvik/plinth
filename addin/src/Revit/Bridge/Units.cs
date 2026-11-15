using System;

namespace B2M.Revit.Bridge
{
    /// <summary>
    /// The add-in speaks millimetres on the wire; Revit's internal unit is the foot.
    /// Every conversion goes through here (porting standard: one unit system, converted in one place).
    /// </summary>
    internal static class Units
    {
        private const double MmPerFoot = 304.8;

        public static double FromMm(double mm) => mm / MmPerFoot;

        public static double ToMm(double feet) => Math.Round(feet * MmPerFoot, 1);
    }
}
