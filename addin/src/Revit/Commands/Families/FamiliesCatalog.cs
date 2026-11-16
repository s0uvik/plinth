using B2M.Revit.Bridge;

namespace B2M.Revit.Commands.Families
{
    /// <summary>Registers the families toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog) and the
    /// B2M-only ensure_family_type and find_family_files.</summary>
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
            dispatcher.Register(new EnsureFamilyTypeHandler());
            dispatcher.Register(new FindFamilyFilesHandler());
        }
    }
}
