using Plinth.Revit.Bridge;

namespace Plinth.Revit.Commands.Links
{
    /// <summary>Registers the links toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class LinksCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new ListLinkedModelsHandler());
            dispatcher.Register(new ListLinkedCadHandler());
            dispatcher.Register(new ImportCadToViewHandler());
            dispatcher.Register(new LinkRevitModelHandler());
            dispatcher.Register(new UnloadLinkHandler());
            dispatcher.Register(new ReloadLinkHandler());
            dispatcher.Register(new GetLinkElementsHandler());
            dispatcher.Register(new GetProjectCoordinateSystemHandler());
            dispatcher.Register(new GetLinkCoordinateSystemHandler());
            dispatcher.Register(new AcquireCoordinatesFromLinkHandler());
            dispatcher.Register(new PublishCoordinatesToLinkHandler());
            dispatcher.Register(new SetProjectBasePointHandler());
        }
    }
}
