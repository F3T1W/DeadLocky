using System.Runtime.CompilerServices;
using System.Text.Json;
using DeadLocky.Entities.Mod.Model;
using DeadLocky.Features.ManageMods.Model;
using DeadLocky.Shared.Api.Diagnostics;

namespace DeadLocky.Features.ManageMods.Api;

public sealed partial class ModWorkspace : IAsyncDisposable
{
    private readonly Action<string> _ensureGameIdle;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root;

    public ModWorkspace(string root, Action<string> ensureGameIdle)
    {
        _root = Path.GetFullPath(root);
        _ensureGameIdle = ensureGameIdle;
        ModFiles.RequireRegularPath(_root);
        _ = Directory.CreateDirectory(_root);
    }

    public async ValueTask DisposeAsync()
    {
        // Let in-flight profile writes finish before releasing the shared semaphore.
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    public ModLibrary Read()
    {
        string path = Path.Combine(_root, "library.json");
        if (!File.Exists(path))
        {
            return ModLibrary.Empty;
        }

        if (new FileInfo(path).Length > 64 * 1024 * 1024)
        {
            throw new IOException("The mod library is too large.");
        }

        ModLibrary library = JsonSerializer.Deserialize(File.ReadAllBytes(path), ModJsonContext.Default.ModLibrary)
                             ?? throw new IOException("The mod library is invalid.");
        ModProfile[] profiles = [ModProfile.Vanilla, .. library.Profiles.Where(static profile => !profile.IsVanilla)];
        return profiles.Select(static profile => profile.Id).Distinct().Count() != profiles.Length
               || library.Packages.Select(static package => package.Id).Distinct().Count() != library.Packages.Length
            ? throw new IOException("The mod library contains duplicate identifiers.")
            : (library with
            {
                Profiles = profiles,
                ActiveProfileId = profiles.Any(profile => profile.Id == library.ActiveProfileId)
                    ? library.ActiveProfileId
                    : Guid.Empty
            });
    }

    public Task CreateProfileAsync(string name, CancellationToken token)
    {
        return ChangeAsync(library =>
        {
            var profile = new ModProfile(Guid.NewGuid(), ValidateName(name, library), []);
            return library with { ActiveProfileId = profile.Id, Profiles = [.. library.Profiles, profile] };
        }, token);
    }

    public Task RenameProfileAsync(Guid id, string name, CancellationToken token)
    {
        return ChangeAsync(library =>
        {
            _ = RequireEditable(library, id);
            string valid = ValidateName(name, library, id);
            return library with
            {
                Profiles =
                [
                    .. library.Profiles.Select(profile =>
                        profile.Id == id ? profile with { Name = valid } : profile)
                ]
            };
        }, token);
    }

    public Task SelectProfileAsync(Guid id, CancellationToken token)
    {
        return ChangeAsync(library => library.Profiles.Any(profile => profile.Id == id)
            ? library with { ActiveProfileId = id }
            : throw new IOException("The selected profile does not exist."), token);
    }

    public Task SetEnabledAsync(Guid profileId, Guid packageId, bool enabled, CancellationToken token)
    {
        return ChangeProfileAsync(profileId, profile => profile with
        {
            Mods =
            [
                .. profile.Mods.Select(mod => mod.PackageId == packageId
                    ? mod with { Enabled = enabled }
                    : mod)
            ]
        }, token, enabled ? "enable" : "disable");
    }

    public Task RemoveAsync(Guid profileId, Guid packageId, CancellationToken token)
    {
        return ChangeProfileAsync(profileId, profile => profile with
        {
            Mods = [.. profile.Mods.Where(mod => mod.PackageId != packageId)]
        }, token);
    }

    public Task MoveAsync(Guid profileId, Guid packageId, int direction, CancellationToken token)
    {
        return ChangeProfileAsync(profileId, profile =>
        {
            ProfileMod[] mods = [.. profile.Mods];
            int current = Array.FindIndex(mods, mod => mod.PackageId == packageId);
            int next = current + direction;
            if (current >= 0 && next >= 0 && next < mods.Length && direction is -1 or 1)
            {
                (mods[current], mods[next]) = (mods[next], mods[current]);
            }

            return profile with { Mods = mods };
        }, token);
    }

    public Task<PreparedMod> PrepareAsync(ModCatalogClient catalog, ModDownload download,
        IProgress<long>? progress, CancellationToken token)
    {
        return AppLog.RunAsync("mod.prepare", () => PrepareCoreAsync(catalog, download, progress, token), AppLog.Data(("download.expectedBytes", download.Size), ("download.name", download.Name)));
    }

    private async Task<PreparedMod> PrepareCoreAsync(ModCatalogClient catalog, ModDownload download,
        IProgress<long>? progress, CancellationToken token)
    {
        var id = Guid.NewGuid();
        string staging = Path.Combine(_root, "Downloads", id.ToString("N"));
        _ = Directory.CreateDirectory(staging);
        try
        {
            string payload = Path.Combine(staging, "payload");
            await catalog.DownloadAsync(download, payload, progress, token);
            string content = Path.Combine(staging, "Content");
            string[] roots = await Task.Run(() => ModPackageExtractor.ExtractAsync(payload, content, token), token);
            return new PreparedMod(id, content, roots);
        }
        catch
        {
            Directory.Delete(staging, true);
            throw;
        }
    }

    public void Discard(PreparedMod prepared)
    {
        string expected = Path.Combine(_root, "Downloads", prepared.Id.ToString("N"));
        if (Path.GetFullPath(prepared.Directory) != Path.Combine(expected, "Content"))
        {
            throw new IOException("Invalid prepared mod directory.");
        }

        if (Directory.Exists(expected))
        {
            Directory.Delete(expected, true);
        }
    }

    public Task InstallAsync(Guid profileId, CatalogMod mod, ModDownload download, PreparedMod prepared,
        string[] selected, CancellationToken token)
    {
        return WithLockAsync(async () =>
        {
            ModLibrary library = Read();
            ModProfile profile = RequireEditable(library, profileId);
            ModRequirements.Validate(library, profileId, mod);
            if (selected.Length == 0 || selected.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length
                                     || selected.Any(name => !prepared.Roots.Contains(name, StringComparer.Ordinal)))
            {
                throw new IOException("Select at least one valid VPK file.");
            }

            AppLog.Event("mod.requirements.verify", "validated");
            string expected = Path.Combine(_root, "Downloads", prepared.Id.ToString("N"), "Content");
            if (Path.GetFullPath(prepared.Directory) != expected)
            {
                throw new IOException("Invalid prepared mod directory.");
            }

            List<ModPackageFile> files = [];
            var conflicts = new ModResourceConflicts(token);
            foreach (string name in selected)
            {
                token.ThrowIfCancellationRequested();
                string source = Path.Combine(expected, ModFiles.SafeRelativePath(name));
                ModFiles.RequireRegularPath(source);
                VpkContents contents = await Task.Run(() => VpkReader.Read(source), token);
                if (conflicts.Add(name, source, contents.Resources, contents) is { } conflict)
                {
                    throw new IOException($"The selected VPKs contain different versions of {conflict.Resource}. "
                                          + "Choose only one variant.");
                }

                AppLog.Event("mod.vpk.verify", "validated", fields: AppLog.Data(("vpk.file", name),
                    ("vpk.resources", contents.Resources.Length), ("vpk.parts", contents.Parts.Length)));
                files.Add(new ModPackageFile(name, ModFiles.Hash(source), contents.Resources,
                    contents.Parts.ToDictionary(part => ModFiles.SafeRelativePath(Path.GetRelativePath(expected, part)),
                        ModFiles.Hash, StringComparer.OrdinalIgnoreCase)));
            }

            var package = new InstalledMod(prepared.Id, mod.RemoteId, mod.Name, mod.Author, download.Name,
                DateTimeOffset.UtcNow, [.. files])
            {
                RequiredModIds =
                [
                    .. ModRequirements.Required(mod)
                        .Select(static dependency => dependency.RemoteId!)
                ]
            };
            string packages = Path.Combine(_root, "Packages");
            _ = Directory.CreateDirectory(packages);
            string destination = Path.Combine(packages, package.Id.ToString("N"));
            Directory.Move(expected, destination);
            try
            {
                ProfileMod[] entries =
                [
                    .. profile.Mods.Where(entry =>
                        library.Packages.Single(item => item.Id == entry.PackageId).RemoteId != mod.RemoteId)
                ];
                ModProfile updated = profile with { Mods = [.. entries, new ProfileMod(package.Id)] };
                Save(library with
                {
                    Packages = [.. library.Packages, package],
                    Profiles = [.. library.Profiles.Select(item => item.Id == profileId ? updated : item)]
                });
            }
            catch
            {
                Directory.Move(destination, expected);
                throw;
            }
        }, token, fields: AppLog.Data(("profile.id", profileId), ("mod.remoteId", mod.RemoteId), ("mod.packageId", prepared.Id), ("download.name", download.Name),
            ("vpk.selected", string.Join(";", selected.Take(16)))));
    }

    public Task ApplyAsync(string directory, CancellationToken token)
    {
        return WithLockAsync(() => Task.Run(() =>
        {
            _ensureGameIdle(directory);
            var deployment = new ModDeployment(_root, _ensureGameIdle);
            deployment.Recover();
            deployment.Apply(directory, Read(), token);
        }, token), token, fields: AppLog.Data(("game.directory", directory)));
    }

    private Task ChangeProfileAsync(Guid id, Func<ModProfile, ModProfile> change, CancellationToken token, [CallerMemberName] string stage = "")
    {
        return ChangeAsync(library =>
        {
            ModProfile updated = change(RequireEditable(library, id));
            AppLog.Event("profile.change", stage, fields: AppLog.Data(("profile.id", id)));
            return library with
            {
                Profiles = [.. library.Profiles.Select(profile => profile.Id == id ? updated : profile)]
            };
        }, token, stage);
    }

    private Task ChangeAsync(Func<ModLibrary, ModLibrary> change, CancellationToken token, [CallerMemberName] string stage = "")
    {
        return WithLockAsync(() =>
        {
            Save(change(Read()));
            return Task.CompletedTask;
        }, token, stage);
    }

    private void Save(ModLibrary library)
    {
        ModFiles.WriteJson(Path.Combine(_root, "library.json"), library, ModJsonContext.Default.ModLibrary);
        AppLog.Event("mods.library.saved", "committed", fields: AppLog.Data(("profile.activeId", library.ActiveProfileId),
            ("profile.count", library.Profiles.Length), ("mod.packages", library.Packages.Length),
            ("profile.enabledPackages", string.Join(";", library.ActiveProfile.Mods.Where(static item => item.Enabled)
                .Take(16).Select(static item => item.PackageId)))));
    }

    private Task WithLockAsync(Func<Task> action, CancellationToken token, [CallerMemberName] string stage = "",
        IReadOnlyDictionary<string, string?>? fields = null)
    {
        return AppLog.RunAsync("mods." + stage, () => WithLockCoreAsync(action, token), fields);
    }

    private async Task WithLockCoreAsync(Func<Task> action, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            ModFiles.RequireRegularPath(_root);
            await using var fileLock = new FileStream(Path.Combine(_root, "workspace.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            token.ThrowIfCancellationRequested();
            await action();
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private static ModProfile RequireEditable(ModLibrary library, Guid id)
    {
        return id == Guid.Empty
            ? throw new IOException("Vanilla is a permanent clean profile. Create your own profile.")
            : library.Profiles.SingleOrDefault(profile => profile.Id == id)
              ?? throw new IOException("The selected profile does not exist.");
    }

    private static string ValidateName(string name, ModLibrary library, Guid? except = null)
    {
        string value = name.Trim();
        return value.Length is 0 or > 60 || value.Any(char.IsControl)
                                         || library.Profiles.Any(profile => profile.Id != except
                                                                            && string.Equals(profile.Name, value,
                                                                                StringComparison.OrdinalIgnoreCase))
            ? throw new IOException("Use a unique profile name between 1 and 60 characters.")
            : value;
    }
}
