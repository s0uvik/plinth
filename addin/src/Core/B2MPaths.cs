using System;
using System.IO;

namespace B2M.Revit.Core
{
    /// <summary>
    /// Every folder the add-in writes to lives under %LOCALAPPDATA%\B2M, shared with the
    /// Python service: one place for discovery files and one log folder (PRD: Supportability).
    /// </summary>
    public static class B2MPaths
    {
        /// <summary>Overrides the root folder; set by tests only.</summary>
        public static string RootOverride { get; set; }

        public static string Root => RootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "B2M");

        public static string Discovery => Path.Combine(Root, "discovery");

        public static string Logs => Path.Combine(Root, "logs");
    }
}
