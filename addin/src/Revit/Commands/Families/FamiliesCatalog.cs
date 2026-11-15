using B2M.Revit.Bridge;

namespace B2M.Revit.Commands.Families
{
    /// <summary>Registers the families toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class FamiliesCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new ListLoadedFamiliesHandler());
            dispatcher.Register(new LoadFamilyFromPathHandler());
            dispatcher.Register(new UnloadFamilyHandler());
            dispatcher.Register(new DuplicateFamilyTypeHandler());
            dispatcher.Register(new RenameFamilyTypeHandler());
            dispatcher.Register(new AuditFamiliesHandler());
            dispatcher.Register(new ReplaceFamilyTypeHandler());
            dispatcher.Register(new GetFamilyInstancesHandler());
            dispatcher.Register(new ListFamilyTypesInFamilyHandler());
            dispatcher.Register(new ExportFamilyToPathHandler());
        }
    }
}
