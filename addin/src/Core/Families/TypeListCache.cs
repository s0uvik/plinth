// New in B2M (Step 10, F1): listing the types in a template means opening it in Revit (seconds for the large Systems
// templates), so list_types_in_file keeps its full answer in %LOCALAPPDATA%\B2M\cache\file-types, one JSON file per
// source file. The key covers the full path, the file's size and last write time, the Revit year (an older file is
// upgraded on open, so ids and names can differ per year) and this format's version, so a changed file is read again.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Core.Families
{
    public static class TypeListCache
    {
        /// <summary>Bumped whenever the cached listing's shape changes, so old entries are ignored.</summary>
        public const int FormatVersion = 1;

        public static string DefaultFolder => Path.Combine(B2MPaths.Root, "cache", "file-types");

        public static string Key(string fullPath, long sizeBytes, DateTime lastWriteUtc, string revitYear)
        {
            var text = (fullPath ?? "").Trim().ToUpperInvariant() + "|" + sizeBytes + "|" + lastWriteUtc.ToUniversalTime().Ticks + "|"
                + (revitYear ?? "") + "|v" + FormatVersion;
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var hex = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }

        public static string PathFor(string folder, string key) => Path.Combine(folder, key + ".json");

        /// <summary>The cached listing, or null when there is none or it cannot be read (it is then rebuilt).</summary>
        public static JObject TryRead(string folder, string key)
        {
            try
            {
                var path = PathFor(folder, key);
                if (!File.Exists(path)) return null;
                var content = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                return content.Value<int?>("format_version") == FormatVersion ? content : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return null;
            }
        }

        /// <summary>Writes the listing (stamped with the format version) through a temporary file; false when it cannot be written.</summary>
        public static bool Write(string folder, string key, JObject listing)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var copy = (JObject)listing.DeepClone();
                copy["format_version"] = FormatVersion;
                var path = PathFor(folder, key);
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, copy.ToString(Formatting.None), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
