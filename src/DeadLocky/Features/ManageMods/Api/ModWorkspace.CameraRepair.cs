using System.IO.Hashing;
using DeadLocky.Entities.Mod.Model;
using DeadLocky.Features.ManageMods.Model;
using DeadLocky.Shared.Api.Diagnostics;

namespace DeadLocky.Features.ManageMods.Api;

public sealed partial class ModWorkspace
{
    public async Task<CameraRepairResult[]> RepairCamerasAsync(string gameDirectory, Guid[] packageIds,
        CancellationToken token)
    {
        List<CameraRepairResult> results = [];
        await WithLockAsync(() => Task.Run(() =>
        {
            _ensureGameIdle(gameDirectory);
            string gameIndex = Path.Combine(gameDirectory, "game", "citadel", "pak01_dir.vpk");
            ModFiles.RequireRegularPath(gameIndex);
            string gameHash = ModFiles.Hash(gameIndex);
            VpkContents game = VpkReader.Read(gameIndex);
            ModLibrary library = Read();
            if (packageIds.Length == 0 || packageIds.Distinct().Count() != packageIds.Length
                || packageIds.Any(id => library.ActiveProfile.Mods.All(mod => mod.PackageId != id)))
            {
                throw new IOException("Select installed mods from the current profile.");
            }

            foreach (Guid id in packageIds)
            {
                token.ThrowIfCancellationRequested();
                InstalledMod original = library.Packages.Single(package => package.Id == id);
                var repairedId = Guid.NewGuid();
                string destination = PackageDirectory(repairedId);
                bool committed = false;
                try
                {
                    _ = Directory.CreateDirectory(destination);
                    List<ModPackageFile> files = [];
                    int models = 0;
                    foreach (ModPackageFile file in original.Files)
                    {
                        token.ThrowIfCancellationRequested();
                        string source = VerifyPackageFile(original, file);
                        VpkContents contents = VpkReader.Read(source);
                        if (contents.Parts.Length > 0 || IsSigned(source))
                        {
                            throw new IOException("Signed or multipart VPKs are not supported by camera repair.");
                        }

                        var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                        foreach ((string name, VpkEntry entry) in contents.Entries
                                     .Where(static pair => pair.Key.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase)))
                        {
                            token.ThrowIfCancellationRequested();
                            if (!game.Entries.TryGetValue(name, out VpkEntry? donor)) { continue; }
                            byte[]? repaired = ModelCameraRepair.Repair(ReadModel(entry), ReadModel(donor), token);
                            if (repaired is not null) { replacements.Add(name, repaired); }
                        }

                        string output = Path.Combine(destination, ModFiles.SafeRelativePath(file.Name));
                        _ = Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                        if (replacements.Count == 0) { File.Copy(source, output); }
                        else
                        {
                            VpkWriter.Replace(source, output, replacements, token);
                            VerifyRepacked(contents, VpkReader.Read(output), replacements, token);
                        }

                        models += replacements.Count;
                        files.Add(file with { Sha256 = ModFiles.Hash(output) });
                    }

                    if (models == 0)
                    {
                        results.Add(new CameraRepairResult(id, original.Name, false,
                            "No recognized outdated camera data. This mod was left unchanged."));
                        continue;
                    }

                    _ensureGameIdle(gameDirectory);
                    if (ModFiles.Hash(gameIndex) != gameHash)
                    {
                        throw new IOException("Steam changed the game files during repair. Retry after the update.");
                    }

                    foreach (ModPackageFile file in original.Files) { _ = VerifyPackageFile(original, file); }
                    InstalledMod repairedPackage = original with
                    {
                        Id = repairedId,
                        Files = [.. files],
                        CameraRepairOriginalPackageId = original.Id
                    };
                    ModLibrary updated = ReplacePackage(library, original.Id, repairedId) with
                    {
                        Packages = [.. library.Packages, repairedPackage]
                    };
                    Save(updated);
                    committed = true;
                    library = updated;
                    results.Add(new CameraRepairResult(repairedId, original.Name, true,
                        $"Repaired {models} model(s). Original saved; applies on Play. Test the camera in Hideout."));
                    AppLog.Event("mods.camera-repair", "committed", fields: AppLog.Data(
                        ("mod.originalPackageId", original.Id), ("mod.repairedPackageId", repairedId), ("models", models)));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    AppLog.Event("mods.camera-repair", "original retained", exception,
                        AppLog.Data(("mod.packageId", original.Id)));
                    results.Add(new CameraRepairResult(id, original.Name, false,
                        "Not repaired: " + exception.Message));
                }
                finally
                {
                    if (!committed && Directory.Exists(destination)) { Directory.Delete(destination, true); }
                }
            }
        }, token), token);
        return [.. results];
    }

    public Task RestoreCameraOriginalsAsync(string gameDirectory, Guid[] packageIds, CancellationToken token)
    {
        return WithLockAsync(() => Task.Run(() =>
        {
            _ensureGameIdle(gameDirectory);
            ModLibrary library = Read();
            if (packageIds.Length == 0 || packageIds.Distinct().Count() != packageIds.Length
                || packageIds.Any(id => library.ActiveProfile.Mods.All(mod => mod.PackageId != id)))
            {
                throw new IOException("Select installed repaired mods from the current profile.");
            }

            foreach (Guid id in packageIds)
            {
                token.ThrowIfCancellationRequested();
                InstalledMod repaired = library.Packages.Single(package => package.Id == id);
                if (repaired.CameraRepairOriginalPackageId is not { } originalId)
                {
                    throw new IOException($"{repaired.Name} has no saved camera-repair original.");
                }

                InstalledMod original = library.Packages.Single(package => package.Id == originalId);
                foreach (ModPackageFile file in original.Files) { _ = VerifyPackageFile(original, file); }
                library = ReplacePackage(library, repaired.Id, original.Id);
            }

            token.ThrowIfCancellationRequested();
            _ensureGameIdle(gameDirectory);
            Save(library);
        }, token), token);
    }

    private string PackageDirectory(Guid id)
    {
        return Path.Combine(_root, "Packages", id.ToString("N"));
    }

    private string VerifyPackageFile(InstalledMod package, ModPackageFile file)
    {
        string path = Path.Combine(PackageDirectory(package.Id), ModFiles.SafeRelativePath(file.Name));
        ModFiles.RequireRegularPath(path);
        if (ModFiles.Hash(path) != file.Sha256)
        {
            throw new IOException($"The cached files for {package.Name} changed. Reinstall the mod.");
        }

        foreach ((string name, string hash) in file.Parts)
        {
            string part = Path.Combine(PackageDirectory(package.Id), ModFiles.SafeRelativePath(name));
            ModFiles.RequireRegularPath(part);
            if (ModFiles.Hash(part) != hash) { throw new IOException("A cached VPK part changed."); }
        }

        return path;
    }

    private static ModLibrary ReplacePackage(ModLibrary library, Guid from, Guid to)
    {
        return library with
        {
            Profiles =
        [
            .. library.Profiles.Select(profile => profile with
            {
                Mods = [.. profile.Mods.Select(mod => mod.PackageId == from ? mod with { PackageId = to } : mod)]
            })
        ]
        };
    }

    private static bool IsSigned(string source)
    {
        using FileStream input = File.OpenRead(source);
        using var reader = new BinaryReader(input);
        input.Position = 4;
        if (reader.ReadUInt32() != 2) { return false; }
        input.Position = 24;
        return reader.ReadUInt32() != 0;
    }

    private static byte[] ReadModel(VpkEntry entry)
    {
        long length = entry.Preload.Length + entry.Length;
        if (length is < 16 or > 64 * 1024 * 1024) { throw new IOException("Unsupported model size."); }
        ModFiles.RequireRegularPath(entry.DataPath);
        byte[] bytes = new byte[(int)length];
        entry.Preload.CopyTo(bytes, 0);
        using FileStream input = File.OpenRead(entry.DataPath);
        input.Position = entry.Offset;
        input.ReadExactly(bytes.AsSpan(entry.Preload.Length));
        return Crc32.HashToUInt32(bytes) != entry.Crc32 ? throw new IOException("Model CRC verification failed.") : bytes;
    }

    private static void VerifyRepacked(VpkContents source, VpkContents output,
        Dictionary<string, byte[]> replacements, CancellationToken token)
    {
        if (!source.Entries.Keys.Order(StringComparer.Ordinal).SequenceEqual(output.Entries.Keys.Order(StringComparer.Ordinal)))
        {
            throw new IOException("Repacking changed the VPK resource list.");
        }

        foreach ((string name, VpkEntry original) in source.Entries)
        {
            token.ThrowIfCancellationRequested();
            VpkEntry current = output.Entries[name];
            if (replacements.TryGetValue(name, out byte[]? expected))
            {
                if (!ReadModel(current).AsSpan().SequenceEqual(expected))
                {
                    throw new IOException("The repaired model failed archive verification.");
                }
            }
            else if (VpkResourceHash.Create("raw", original, token) != VpkResourceHash.Create("raw", current, token))
            {
                throw new IOException($"Repacking changed an unrelated resource: {name}.");
            }
        }
    }
}
