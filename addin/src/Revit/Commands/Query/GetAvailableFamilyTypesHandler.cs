// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Families/GetFamilyTypesHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: filter by builtInCategory (OST_Doors...) as well as by display name, because
// display names change with Revit's language ("Puertas"); with builtInCategory, system types such as
// wall and floor types are included, so a client can check every type a build plan needs in one call.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Query
{
    public sealed class GetAvailableFamilyTypesHandler : IRevitCommand
    {
        public string Name => "get_available_family_types";
        public string Description =>
            "List types in the project grouped by category. builtInCategory (e.g. OST_Doors, OST_Walls, OST_Floors) also includes system types; "
            + "category filters loadable family types by display name.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""category"":{""type"":""string""},
            ""builtInCategory"":{""type"":""string""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var categoryName = p.Value<string>("category");
            var builtInName = p.Value<string>("builtInCategory");
            IQueryable<ElementType> types;
            if (!string.IsNullOrEmpty(builtInName))
            {
                if (!Lookup.TryBuiltInCategory(builtInName, out var bic, out var failure)) return failure;
                types = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsElementType()
                    .Cast<ElementType>().AsQueryable();
            }
            else
            {
                types = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<ElementType>().AsQueryable()
                    .Where(t => string.IsNullOrEmpty(categoryName)
                        || (t.Category != null && t.Category.Name.IndexOf(categoryName, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            var groups = types.ToList()
                .GroupBy(t => t.Category?.Name ?? "Uncategorized")
                .OrderBy(g => g.Key)
                .Select(g => new
                {
                    category = g.Key,
                    builtInCategory = Lookup.BuiltInName(g.First().Category),
                    count = g.Count(),
                    types = g.OrderBy(t => t.FamilyName).ThenBy(t => t.Name).Select(t => new
                    {
                        typeId = RevitCompat.GetId(t.Id),
                        familyName = t.FamilyName,
                        typeName = t.Name
                    }).ToArray()
                })
                .ToArray();

            return CommandResult.Ok(new
            {
                totalCategories = groups.Length,
                totalTypes = groups.Sum(g => g.count),
                categories = groups
            });
        }
    }
}
