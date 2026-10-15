using System.Text;
using System.Text.Json;
using DeadLocky.Entities.Mod.Model;

namespace DeadLocky.Features.ManageMods.Api;

internal sealed class ModDeployment(string root, Action<string> ensureGameIdle)
{
    private const string OwnerFile = ".deadlocky-owner.json";
    private static readonly UTF8Encoding Encoding = new(false, true);
    private string JournalPath => Path.Combine(root, "deployment.json");

    public void Apply(string gameDirectory, ModLibrary library, CancellationToken token)
    {
        string game = Path.GetFullPath(gameDirectory);
        string citadel = Path.Combine(game, "game", "citadel");
        string config = Path.Combine(citadel, "gameinfo.gi");
        ModFiles.RequireRegularPath(config);
        if (!File.Exists(Path.Combine(game, "game", "bin", "win64", "deadlock.exe")) || !File.Exists(config))
        {
            throw new IOException("Select a complete Deadlock installation before applying mods.");
        }

        string owned = Path.Combine(citadel, "DeadLockyAddons");
        ValidateOwnedDirectory(owned);
        InstalledMod[] enabled =
        [
            .. library.ActiveProfile.Mods.Where(static mod => mod.Enabled)
                .Select(mod => library.Packages.Single(package => package.Id == mod.PackageId))
        ];
        string[] enabledIds = [.. enabled.Select(static package => package.RemoteId)];
        foreach (InstalledMod package in enabled)
        {
            if (package.RequiredModIds?.Any(id => !enabledIds.Contains(id)) == true)
            {
                throw new IOException($"Enable the required mods for {package.Name} before launching this profile.");
            }
        }

        var conflicts = new ModResourceConflicts(token, true);
        foreach (InstalledMod package in enabled)
        {
            foreach (ModPackageFile file in package.Files)
            {
                string source = Path.Combine(root, "Packages", package.Id.ToString("N"),
                    ModFiles.SafeRelativePath(file.Name));
                if (conflicts.Add(package.Name, source, file.Resources) is { } conflict)
                {
                    throw new IOException($"Mod conflict: {conflict.FirstMod} and {conflict.SecondMod} contain "
                                          + $"different content at {conflict.Resource}. Disable one of these mods.");
                }
            }
        }

        if (enabled.Sum(static package => package.Files.Length) > 99)
        {
            throw new IOException("A profile supports at most 99 enabled VPK archives.");
        }

        byte[]? mergedImages = conflicts.ImageLists.Count > 1
            ? PanoramaImageList.Merge(conflicts.ImageLists.Values, token)
            : null;

        byte[] original = File.ReadAllBytes(config);
        byte[] patched = Encoding.GetBytes(GameSearchPaths.SetEnabled(
            Encoding.GetString(original), enabled.Length > 0));
        if (enabled.Length == 0 && !Directory.Exists(owned) && original.AsSpan().SequenceEqual(patched))
        {
            return;
        }

        string id = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(citadel, ".DeadLockyStage-" + id);
        string backup = Path.Combine(citadel, ".DeadLockyBackup-" + id);
        bool journalWritten = false;
        try
        {
            if (enabled.Length > 0)
            {
                _ = Directory.CreateDirectory(staging);
                var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int index = 0;
                foreach (InstalledMod package in enabled)
                {
                    foreach (ModPackageFile file in package.Files)
                    {
                        token.ThrowIfCancellationRequested();
                        string stem = $"pak{++index:00}";
                        string source = Verify(file.Name, file.Sha256);
                        foreach ((string name, string hash) in file.Parts)
                        {
                            _ = Verify(name, hash);
                        }

                        string target = stem + "_dir.vpk";
                        string output = Path.Combine(staging, target);
                        if (mergedImages is not null && conflicts.ImageLists.ContainsKey(source))
                        {
                            VpkWriter.Replace(source, output, PanoramaImageList.ResourcePath, mergedImages, token);
                            hashes.Add(target, ModFiles.Hash(output));
                        }
                        else
                        {
                            Copy(file.Name, target, file.Sha256);
                            foreach ((string name, string hash) in file.Parts)
                            {
                                Copy(name, stem + name[name.LastIndexOf('_')..], hash);
                            }
                        }
                    }

                    continue;

                    void Copy(string name, string target, string hash)
                    {
                        string output = Path.Combine(staging, target);
                        File.Copy(Verify(name, hash), output);
                        hashes.Add(target, ModFiles.Hash(output));
                    }

                    string Verify(string name, string hash)
                    {
                        string source = Path.Combine(root, "Packages", package.Id.ToString("N"),
                            ModFiles.SafeRelativePath(name));
                        ModFiles.RequireRegularPath(source);
                        return !File.Exists(source) || ModFiles.Hash(source) != hash
                            ? throw new IOException($"The cached files for {package.Name} changed. Reinstall the mod.")
                            : source;
                    }
                }

                ModFiles.WriteJson(Path.Combine(staging, OwnerFile), new OwnedModFiles(hashes),
                    ModJsonContext.Default.OwnedModFiles);
            }

            var journal = new DeploymentJournal(game, id, original, Directory.Exists(owned));
            ModFiles.WriteJson(JournalPath, journal, ModJsonContext.Default.DeploymentJournal);
            journalWritten = true;
            token.ThrowIfCancellationRequested();
            ensureGameIdle(game);
            if (!File.ReadAllBytes(config).AsSpan().SequenceEqual(original))
            {
                throw new IOException("Steam changed gameinfo.gi while mods were being prepared. "
                                      + "Retry after the update.");
            }

            if (journal.HadOwnedDirectory)
            {
                Directory.Move(owned, backup);
            }

            if (enabled.Length > 0)
            {
                Directory.Move(staging, owned);
            }

            ModFiles.AtomicWrite(config, patched);
            ModFiles.WriteJson(JournalPath, journal with { Committed = true },
                ModJsonContext.Default.DeploymentJournal);
            Recover();
        }
        catch
        {
            if (journalWritten)
            {
                Recover();
            }

            throw;
        }
        finally
        {
            if (!File.Exists(JournalPath) && Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }
        }
    }

    public void Recover()
    {
        if (!File.Exists(JournalPath))
        {
            return;
        }

        DeploymentJournal journal = JsonSerializer.Deserialize(File.ReadAllBytes(JournalPath),
            ModJsonContext.Default.DeploymentJournal) ?? throw new IOException("Invalid mod recovery journal.");
        if (!Guid.TryParseExact(journal.TransactionId, "N", out _))
        {
            throw new IOException("Invalid mod recovery transaction.");
        }

        string citadel = Path.Combine(Path.GetFullPath(journal.GameDirectory), "game", "citadel");
        string config = Path.Combine(citadel, "gameinfo.gi");
        ModFiles.RequireRegularPath(config);
        ensureGameIdle(journal.GameDirectory);
        string owned = Path.Combine(citadel, "DeadLockyAddons");
        string backup = Path.Combine(citadel, ".DeadLockyBackup-" + journal.TransactionId);
        string staging = Path.Combine(citadel, ".DeadLockyStage-" + journal.TransactionId);
        if (!journal.Committed)
        {
            if (Directory.Exists(backup))
            {
                DeleteOwnedDirectory(owned);
                ValidateOwnedDirectory(backup);
                Directory.Move(backup, owned);
            }
            else if (!journal.HadOwnedDirectory)
            {
                DeleteOwnedDirectory(owned);
            }

            byte[] current = File.ReadAllBytes(config);
            if (!current.AsSpan().SequenceEqual(journal.OriginalGameInfo))
            {
                string original = Encoding.GetString(journal.OriginalGameInfo);
                string expected = GameSearchPaths.SetEnabled(original, !journal.HadOwnedDirectory);
                byte[] restored = current.AsSpan().SequenceEqual(Encoding.GetBytes(expected))
                    ? journal.OriginalGameInfo
                    : Encoding.GetBytes(GameSearchPaths.SetEnabled(
                        Encoding.GetString(current), journal.HadOwnedDirectory));
                ModFiles.AtomicWrite(config, restored);
            }
        }

        DeleteOwnedDirectory(backup);
        DeleteOwnedDirectory(staging);
        File.Delete(JournalPath);
    }

    private static void DeleteOwnedDirectory(string path)
    {
        ValidateOwnedDirectory(path);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    private static void ValidateOwnedDirectory(string path)
    {
        ModFiles.RequireRegularPath(path);
        if (!Directory.Exists(path))
        {
            return;
        }

        string marker = Path.Combine(path, OwnerFile);
        if (!File.Exists(marker) || new FileInfo(marker).Length > 1024 * 1024)
        {
            throw new IOException("The mod directory is not owned by DeadLocky. Its files were left untouched.");
        }

        ModFiles.RequireRegularPath(marker);
        OwnedModFiles manifest = JsonSerializer.Deserialize(File.ReadAllBytes(marker),
            ModJsonContext.Default.OwnedModFiles) ?? throw new IOException("Invalid mod ownership manifest.");
        string[] files = Directory.GetFileSystemEntries(path);
        if (files.Length != manifest.Hashes.Count + 1)
        {
            throw new IOException("Unmanaged files were added to the DeadLocky mod directory. "
                                  + "They were left untouched.");
        }

        foreach ((string name, string hash) in manifest.Hashes)
        {
            if (Path.GetFileName(name) != name || name == OwnerFile)
            {
                throw new IOException("Invalid mod ownership filename.");
            }

            string file = Path.Combine(path, name);
            ModFiles.RequireRegularPath(file);
            if (!File.Exists(file) || ModFiles.Hash(file) != hash)
            {
                throw new IOException("An installed mod was changed outside DeadLocky. Its files were left untouched.");
            }
        }
    }
}
