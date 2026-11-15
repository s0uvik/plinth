using B2M.Revit.Bridge;

namespace B2M.Revit.Commands.View
{
    /// <summary>Registers the view toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class ViewCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new AnalyzeSheetLayoutHandler());
            dispatcher.Register(new SetViewCropHandler());
            dispatcher.Register(new SetViewScaleHandler());
            dispatcher.Register(new ActivateViewHandler());
            dispatcher.Register(new ShowElementInViewHandler());
            dispatcher.Register(new SetViewStyleHandler());
            dispatcher.Register(new SetViewOrientationHandler());
            dispatcher.Register(new CreateCameraViewHandler());
        }
    }
}
