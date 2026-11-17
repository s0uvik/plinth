using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Core.Execution
{
    /// <summary>
    /// Builds response lines. Every response has <c>id</c> and <c>success</c>; failures carry
    /// <c>error</c> as "CODE: message" plus optional data (valid options, partial results).
    /// </summary>
    public static class Envelope
    {
        /// <summary>Largest response line the add-in sends (PRD: 1 MiB response cap).</summary>
        public const int MaxResponseChars = 1024 * 1024;

        public static JObject Ok(string id, JToken data) => new JObject
        {
            ["id"] = id,
            ["success"] = true,
            ["data"] = data ?? JValue.CreateNull()
        };

        public static JObject Error(string id, string error, JToken data = null)
        {
            var envelope = new JObject
            {
                ["id"] = id,
                ["success"] = false,
                ["error"] = error
            };
            if (data != null) envelope["data"] = data;
            return envelope;
        }

        public static string Line(JObject envelope) => envelope.ToString(Formatting.None);

        public static string ErrorLine(string id, string error, JToken data = null) => Line(Error(id, error, data));
    }
}
