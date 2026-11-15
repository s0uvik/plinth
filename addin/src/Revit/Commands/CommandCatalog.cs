using B2M.Revit.Bridge;
using B2M.Revit.Commands.Create;
using B2M.Revit.Commands.Meta;
using B2M.Revit.Commands.Query;

namespace B2M.Revit.Commands
{
    /// <summary>Every command the add-in serves, registered in one place.</summary>
    internal static class CommandCatalog
    {
        public static CommandDispatcher Create()
        {
            var dispatcher = new CommandDispatcher();
            // query
            dispatcher.Register(new GetCurrentViewInfoHandler());
            // create
            dispatcher.Register(new CreateLevelHandler());
            dispatcher.Register(new CreateLineBasedElementHandler());
            // meta
            dispatcher.Register(new BatchExecuteHandler(dispatcher));
            return dispatcher;
        }
    }
}
