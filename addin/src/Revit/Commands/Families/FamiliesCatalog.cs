using B2M.Revit.Bridge;

namespace B2M.Revit.Commands.Families
{
    /// <summary>Registers the families toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog) and the
    /// B2M-only ensure_family_type and find_family_files, the system-type counterparts ensure_wall_type and ensure_floor_type,
    /// the template-content commands list_template_files, list_types_in_file and copy_types_from_file, and measure_family_types
    /// (types are this toolset's concern; their creation counterpart create_foundation_slab is in CreateCatalog).</summary>
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
            // New in B2M (Step 9 T7c): Basic Wall and Floor / foundation slab types by thickness (HostTypes.cs).
            dispatcher.Register(new EnsureWallTypeHandler());
            dispatcher.Register(new EnsureFloorTypeHandler());
            // New in B2M (Step 10 F1): families and MEP types from Revit's installed templates (no local library in Revit 2026).
            dispatcher.Register(new ListTemplateFilesHandler());
            dispatcher.Register(new ListTypesInFileHandler());
            dispatcher.Register(new CopyTypesFromFileHandler());
            // New in B2M (Step 10, families first): each family type's box about its insertion point, measured and rolled back.
            dispatcher.Register(new MeasureFamilyTypesHandler());
        }
    }
}
