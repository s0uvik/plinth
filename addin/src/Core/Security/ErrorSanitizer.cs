// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Security/ErrorSanitizer.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: namespace only.

using System.Text.RegularExpressions;

namespace Plinth.Revit.Core.Security
{
    /// <summary>
    /// Strips absolute paths from error strings before they reach a response or log,
    /// keeping only the file name.
    /// </summary>
    public static class ErrorSanitizer
    {
        private static readonly Regex WindowsAbsolute = new Regex(
            @"[A-Za-z]:\\(?:[^\\""'\s]+\\)*([^\\""'\s]+)", RegexOptions.Compiled);

        private static readonly Regex Unc = new Regex(
            @"\\\\[^\\""'\s]+\\(?:[^\\""'\s]+\\)*([^\\""'\s]+)", RegexOptions.Compiled);

        private static readonly Regex UnixHome = new Regex(
            @"/(?:home|Users)/[^/\s""']+/(?:[^/\s""']+/)*([^/\s""']+)", RegexOptions.Compiled);

        public static string Sanitize(string error)
        {
            if (string.IsNullOrEmpty(error)) return error;
            // UNC must run before WindowsAbsolute so the leading \\server\ is not mis-matched.
            error = Unc.Replace(error, "$1");
            error = WindowsAbsolute.Replace(error, "$1");
            error = UnixHome.Replace(error, "$1");
            return error;
        }
    }
}
