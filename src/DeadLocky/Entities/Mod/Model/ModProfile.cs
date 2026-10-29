namespace DeadLocky.Entities.Mod.Model;

public sealed record ModProfile(Guid Id, string Name, ProfileMod[] Mods)
{
    public static ModProfile Vanilla { get; } = new(Guid.Empty, "Vanilla", []);
    public bool IsVanilla => Id == Guid.Empty;
}

public sealed record ProfileMod(Guid PackageId, bool Enabled = true);

public sealed record InstalledMod(
    Guid Id,
    string RemoteId,
    string Name,
    string Author,
    string DownloadName,
    DateTimeOffset InstalledAt,
    ModPackageFile[] Files)
{
    public string[]? RequiredModIds { get; init; }
    public Guid? CameraRepairOriginalPackageId { get; init; }
}

public sealed record ModPackageFile(string Name, string Sha256, string[] Resources, Dictionary<string, string> Parts);

public sealed record ModLibrary(Guid ActiveProfileId, ModProfile[] Profiles, InstalledMod[] Packages)
{
    public static ModLibrary Empty => new(Guid.Empty, [ModProfile.Vanilla], []);
    public ModProfile ActiveProfile => Profiles.Single(profile => profile.Id == ActiveProfileId);
}
