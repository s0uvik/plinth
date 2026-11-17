using Plinth.Revit.Bridge;

namespace Plinth.Revit.Commands.Annotation
{
    /// <summary>Registers the annotation toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog) and the Step 9 drawing commands.</summary>
    internal static class AnnotationCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new TagAllWallsHandler());
            dispatcher.Register(new TagElementsHandler());
            dispatcher.Register(new TagAllByCategoryHandler());
            dispatcher.Register(new CreateTextNoteHandler());
            dispatcher.Register(new CreateDimensionsHandler());
            dispatcher.Register(new CreateFilledRegionHandler());
            dispatcher.Register(new CreateDetailLineHandler());
            dispatcher.Register(new CreateCalloutViewHandler());
            dispatcher.Register(new ListKeynotesHandler());
            dispatcher.Register(new ApplyKeynoteToElementHandler());
            dispatcher.Register(new FindUntaggedElementsHandler());
            dispatcher.Register(new FindUndimensionedElementsHandler());
            dispatcher.Register(new WipeEmptyTagsHandler());
            dispatcher.Register(new AutoDimensionPlanHandler());
            dispatcher.Register(new AutoDimensionSectionHandler());
            dispatcher.Register(new LabelRoomsHandler());
            dispatcher.Register(new SpotLevelHandler());
        }
    }
}
