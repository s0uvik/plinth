using Plinth.Revit.Bridge;

namespace Plinth.Revit.Commands.Query
{
    /// <summary>Registers the query toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class QueryCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new GetSelectedElementsHandler());
            dispatcher.Register(new AiElementFilterHandler());
            dispatcher.Register(new GetMaterialQuantitiesHandler());
            dispatcher.Register(new GetElementDetailsHandler());
            dispatcher.Register(new GetElementParametersHandler());
            dispatcher.Register(new GetTypeParametersHandler());
            dispatcher.Register(new ListProjectParametersHandler());
            dispatcher.Register(new GetElementRelationshipsHandler());
            dispatcher.Register(new SurveyChangeImpactHandler());
            dispatcher.Register(new ListGroupsHandler());
            dispatcher.Register(new GetGroupMembersHandler());
            dispatcher.Register(new ListAssembliesHandler());
            dispatcher.Register(new GetAssemblyMembersHandler());
            dispatcher.Register(new ListWorksetsHandler());
            dispatcher.Register(new ReadBackElementsHandler());
        }
    }
}
