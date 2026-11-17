using System;
using System.IO;

namespace Plinth.Revit.Core
{
    /// <summary>
    /// Every folder the add-in writes to lives under %LOCALAPPDATA%\Plinth, shared with the
    /// Python service: one place for discovery files and one log folder (PRD: Supportability).
    /// </summary>
    public static class PlinthPaths
    {
        /// <summary>Overrides the root folder; set by tests only.</summary>
        public static string RootOverride { get; set; }

        public static string Root => RootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Plinth");

        public static string Discovery => Path.Combine(Root, "discovery");

        public static string Logs => Path.Combine(Root, "logs");
    }
}
