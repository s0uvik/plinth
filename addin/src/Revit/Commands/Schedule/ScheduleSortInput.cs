// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Schedules/ScheduleSortInput.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: namespace only.

using System;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Schedule
{
    internal static class ScheduleSortInput
    {
        public static bool Ascending(JObject spec)
        {
            bool? ascending = spec.Value<bool?>("ascending");
            foreach (var key in new[] { "order", "sortOrder" })
            {
                var token = spec[key];
                if (token == null || token.Type == JTokenType.Null) continue;
                var text = token.Type == JTokenType.String ? token.Value<string>().Trim() : null;
                bool direction;
                if (string.Equals(text, "ascending", StringComparison.OrdinalIgnoreCase)) direction = true;
                else if (string.Equals(text, "descending", StringComparison.OrdinalIgnoreCase)) direction = false;
                else throw new ArgumentException(key + " must be Ascending or Descending.");
                if (ascending.HasValue && ascending.Value != direction)
                    throw new ArgumentException("Conflicting ascending/order/sortOrder directions.");
                ascending = direction;
            }
            return ascending ?? true;
        }
    }
}
