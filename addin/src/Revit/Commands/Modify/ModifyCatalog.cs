using Plinth.Revit.Bridge;

namespace Plinth.Revit.Commands.Modify
{
    /// <summary>Registers the modify toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class ModifyCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new MoveElementsHandler());
            dispatcher.Register(new RotateElementsHandler());
            dispatcher.Register(new OperateElementHandler());
            dispatcher.Register(new ColorElementsHandler());
            dispatcher.Register(new SetElementParameterValuesHandler());
            dispatcher.Register(new SetTypeParameterValuesHandler());
            dispatcher.Register(new ChangeElementTypeHandler());
            dispatcher.Register(new AssignElementsToWorksetHandler());
            dispatcher.Register(new SetWallTopHandler());
            dispatcher.Register(new CopyElementsHandler());
            dispatcher.Register(new MirrorElementsHandler());
        }
    }
}
