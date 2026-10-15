using DeadLocky.Features.ManageMods.Model;

namespace DeadLocky.Features.ManageMods.Api;

internal sealed record ModResourceConflict(string Resource, string FirstMod, string SecondMod);

internal sealed class ModResourceConflicts(CancellationToken token, bool mergeImageLists = false)
{
    private readonly Dictionary<(string Archive, string Resource), string> _hashes = [];
    private readonly Dictionary<string, VpkContents> _indexes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Name, string Archive)> _owners = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, VpkEntry> ImageLists { get; } = new(StringComparer.Ordinal);

    public ModResourceConflict? Add(string name, string archive, IEnumerable<string> resources,
        VpkContents? contents = null)
    {
        if (contents is not null)
        {
            _indexes[archive] = contents;
        }

        foreach (string resource in resources.Where(ModResourcePaths.IsGameContent))
        {
            token.ThrowIfCancellationRequested();
            if (mergeImageLists && resource.Equals(PanoramaImageList.ResourcePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                ImageLists[archive] = Index(archive).Entries[resource];
                continue;
            }

            if (_owners.TryAdd(resource, (name, archive)))
            {
                continue;
            }

            (string first, string previous) = _owners[resource];
            if (Fingerprint(previous, resource) != Fingerprint(archive, resource))
            {
                return new ModResourceConflict(resource, first, name);
            }
        }

        return null;
    }

    private string Fingerprint(string archive, string resource)
    {
        if (_hashes.TryGetValue((archive, resource), out string? hash))
        {
            return hash;
        }

        VpkContents index = Index(archive);
        if (!index.Entries.TryGetValue(resource, out VpkEntry? entry))
        {
            throw new IOException("A cached mod's resource index changed. Reinstall the mod.");
        }

        hash = VpkResourceHash.Create(resource, entry, token);
        _hashes.Add((archive, resource), hash);
        return hash;
    }

    private VpkContents Index(string archive)
    {
        if (_indexes.TryGetValue(archive, out VpkContents? index))
        {
            return index;
        }

        ModFiles.RequireRegularPath(archive);
        index = VpkReader.Read(archive);
        _indexes.Add(archive, index);
        return index;
    }
}
