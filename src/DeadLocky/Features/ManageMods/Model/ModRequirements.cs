using DeadLocky.Entities.Mod.Model;

namespace DeadLocky.Features.ManageMods.Model;

internal static class ModRequirements
{
    public static ModDependency[] Required(CatalogMod mod)
    {
        return
        [
            .. (mod.Dependencies ?? []).Where(static dependency =>
                !string.IsNullOrEmpty(dependency.RemoteId) && dependency.Level is null or "required")
        ];
    }

    public static void Validate(ModLibrary library, Guid profileId, CatalogMod mod)
    {
        ModProfile profile = library.Profiles.Single(item => item.Id == profileId);
        string[] enabled =
        [
            .. profile.Mods.Where(static entry => entry.Enabled).Select(entry =>
                library.Packages.Single(package => package.Id == entry.PackageId).RemoteId)
        ];
        string[] missing =
        [
            .. Required(mod).Where(dependency => !enabled.Contains(dependency.RemoteId))
                .Select(static dependency => dependency.Label)
        ];
        if (missing.Length > 0)
        {
            throw new IOException("Install these required mods in this profile first: " + string.Join(", ", missing));
        }
    }
}
