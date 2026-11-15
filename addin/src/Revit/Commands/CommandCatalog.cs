using B2M.Revit.Bridge;

namespace B2M.Revit.Commands
{
    /// <summary>Every command the add-in serves, registered in one place.</summary>
    internal static class CommandCatalog
    {
        public static CommandDispatcher Create()
        {
            var dispatcher = new CommandDispatcher();
            return dispatcher;
        }
    }
}
