using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using DeadLocky.Entities.Mod.Model;
using DeadLocky.Features.LaunchGame.Api;
using DeadLocky.Features.ManageMods.Api;
using DeadLocky.Features.ManageMods.Model;

namespace DeadLocky.UnitTests;

public sealed class ModManagerTests : IDisposable
{
    private const string GameInfo = "\"GameInfo\"\r\n{\r\n\tFileSystem\r\n\t{\r\n\t\tSearchPaths\r\n\t\t{\r\n"
                                    + "\t\t\tGame citadel\r\n\t\t\tGame core\r\n\t\t}\r\n\t}\r\n}\r\n";

    private static readonly string[] Expected = ["Blue/skin.vpk", "Red/skin.vpk"];

    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS()
            ? MacGameProcessScanner.CanonicalPath(Path.GetTempPath())
            : Path.GetTempPath(),
        "DeadLockyMods-" + Guid.NewGuid());

    private readonly List<ModWorkspace> _workspaces = [];

    public void Dispose()
    {
        foreach (ModWorkspace workspace in _workspaces)
        {
            workspace.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public async Task VanillaIsPermanentAndProfilesPersistAcrossRestart()
    {
        ModWorkspace workspace = Workspace();
        Assert.True(workspace.Read().ActiveProfile.IsVanilla);
        _ = await Assert.ThrowsAsync<IOException>(() =>
            workspace.RenameProfileAsync(Guid.Empty, "Changed", CancellationToken.None));
        _ = await Assert.ThrowsAsync<IOException>(() =>
            workspace.SetEnabledAsync(Guid.Empty, Guid.NewGuid(), true, CancellationToken.None));
        await workspace.CreateProfileAsync("  My profile  ", CancellationToken.None);
        Guid id = workspace.Read().ActiveProfileId;
        await workspace.RenameProfileAsync(id, "Custom", CancellationToken.None);
        Assert.Equal("Custom", Workspace().Read().ActiveProfile.Name);
        Assert.Equal(id, Workspace().Read().ActiveProfileId);
        _ = await Assert.ThrowsAsync<IOException>(() =>
            workspace.CreateProfileAsync("vanilla", CancellationToken.None));
        _ = await Assert.ThrowsAsync<IOException>(() => workspace.CreateProfileAsync("CUSTOM", CancellationToken.None));
        await workspace.SelectProfileAsync(Guid.Empty, CancellationToken.None);
        Assert.Empty(Workspace().Read().ActiveProfile.Mods);
        Assert.Equal("Vanilla", Workspace().Read().ActiveProfile.Name);
    }

    [Fact]
    public async Task ModMembershipAndEnablementAreIndependentAcrossProfiles()
    {
        ModWorkspace workspace = Workspace();
        await workspace.CreateProfileAsync("First", CancellationToken.None);
        Guid first = workspace.Read().ActiveProfileId;
        await InstallAsync(workspace, "1", "First skin");
        Guid package = workspace.Read().Packages.Single().Id;
        await workspace.CreateProfileAsync("Second", CancellationToken.None);
        Assert.Empty(workspace.Read().ActiveProfile.Mods);
        await InstallAsync(workspace, "2", "Second skin", "different");
        await workspace.SetEnabledAsync(first, package, false, CancellationToken.None);
        Assert.True(workspace.Read().ActiveProfile.Mods.Single().Enabled);
        await workspace.SelectProfileAsync(first, CancellationToken.None);
        Assert.False(workspace.Read().ActiveProfile.Mods.Single().Enabled);
        await workspace.RemoveAsync(first, package, CancellationToken.None);
        Assert.Empty(workspace.Read().ActiveProfile.Mods);
        Assert.Equal(2, workspace.Read().Packages.Length);
    }

    [Fact]
    public async Task ApplyingAndReturningToVanillaRestoresExactConfigurationAndPreservesForeignFiles()
    {
        string game = Game();
        string foreign = Path.Combine(game, "game", "citadel", "unrelated.vpk");
        await File.WriteAllTextAsync(foreign, "keep");
        ModWorkspace workspace = Workspace();
        await workspace.CreateProfileAsync("Modded", CancellationToken.None);
        await InstallAsync(workspace, "1", "Skin");
        await workspace.ApplyAsync(game, CancellationToken.None);
        Assert.Contains(GameSearchPaths.ModPath, await File.ReadAllTextAsync(Config(game)));
        Assert.True(File.Exists(Path.Combine(Owned(game), "pak01_dir.vpk")));
        await workspace.ApplyAsync(game, CancellationToken.None);
        await workspace.SelectProfileAsync(Guid.Empty, CancellationToken.None);
        await workspace.ApplyAsync(game, CancellationToken.None);
        Assert.Equal(GameInfo, await File.ReadAllTextAsync(Config(game)));
        Assert.False(Directory.Exists(Owned(game)));
        Assert.Equal("keep", await File.ReadAllTextAsync(foreign));
        Assert.False(File.Exists(Path.Combine(_root, "Library", "deployment.json")));
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void ModSearchPathKeepsBaseGameConfigurationAndWritePaths(string newline)
    {
        string original = GameInfo.Replace("\r\n", newline, StringComparison.Ordinal);
        string modified = GameSearchPaths.SetEnabled(original, true);
        Assert.Contains("Mod\t\t\t\t\"citadel\" // DeadLocky", modified);
        Assert.Contains("Write\t\t\t\"citadel\" // DeadLocky", modified);
        Assert.Equal(modified, GameSearchPaths.SetEnabled(modified, true));
        Assert.Equal(original, GameSearchPaths.SetEnabled(modified, false));
    }

    [Fact]
    public void ExplicitSearchPathRolesArePreservedWithoutAddingFallbacks()
    {
        string original = GameInfo.Replace("Game citadel", "\"Game+Mod\" citadel\r\n\t\t\tWrite core",
            StringComparison.Ordinal);
        string modified = GameSearchPaths.SetEnabled(original, true);
        Assert.DoesNotContain("// DeadLocky", modified);
        Assert.Contains("\"Game+Mod\" citadel", modified);
        Assert.Contains("Write core", modified);
        Assert.Equal(original, GameSearchPaths.SetEnabled(modified, false));
    }

    [Fact]
    public async Task ConflictingModsAreRejectedWithoutChangingTheGame()
    {
        string game = Game();
        ModWorkspace workspace = Workspace();
        await workspace.CreateProfileAsync("Conflicts", CancellationToken.None);
        await InstallAsync(workspace, "1", "First");
        await InstallAsync(workspace, "2", "Second", dataValue: 43);
        IOException error =
            await Assert.ThrowsAsync<IOException>(() => workspace.ApplyAsync(game, CancellationToken.None));
        Assert.Contains("Mod conflict", error.Message);
        Assert.Equal(GameInfo, await File.ReadAllTextAsync(Config(game)));
        Assert.False(Directory.Exists(Owned(game)));
    }

    [Fact]
    public async Task IdenticalSharedGameResourcesDoNotBlockDeployment()
    {
        string game = Game();
        ModWorkspace workspace = Workspace();
        await workspace.CreateProfileAsync("Shared", CancellationToken.None);
        await InstallAsync(workspace, "1", "First");
        await InstallAsync(workspace, "2", "Second");
        await workspace.ApplyAsync(game, CancellationToken.None);
        Assert.Equal(2, Directory.GetFiles(Owned(game), "*.vpk").Length);
        await workspace.SelectProfileAsync(Guid.Empty, CancellationToken.None);
        await workspace.ApplyAsync(game, CancellationToken.None);
        Assert.Equal(GameInfo, await File.ReadAllTextAsync(Config(game)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectedVpkFilesCanShareContentButRejectDifferentVariants(bool identical)
    {
        ModWorkspace workspace = Workspace();
        await workspace.CreateProfileAsync("Variants", CancellationToken.None);
        var id = Guid.NewGuid();
        string content = Path.Combine(_root, "Library", "Downloads", id.ToString("N"), "Content");
        _ = Directory.CreateDirectory(content);
        byte[] first = Vpk("shared");
        byte[] second = Vpk("shared");
        second[^1] = identical ? (byte)42 : (byte)43;
        await File.WriteAllBytesAsync(Path.Combine(content, "first.vpk"), first);
        await File.WriteAllBytesAsync(Path.Combine(content, "second.vpk"), second);
        var prepared = new PreparedMod(id, content, ["first.vpk", "second.vpk"]);
        var mod = new CatalogMod { RemoteId = "1", Name = "Shared assets" };
        var download = new ModDownload("https://example.com/mod.zip", 0, "mod.zip", null, null);
        Task install = workspace.InstallAsync(workspace.Read().ActiveProfileId, mod, download, prepared,
            prepared.Roots, CancellationToken.None);
        if (identical)
        {
            await install;
            Assert.Equal(2, workspace.Read().Packages.Single().Files.Length);
        }
        else
        {
            _ = await Assert.ThrowsAsync<IOException>(() => install);
            Assert.Empty(workspace.Read().Packages);
        }
    }

    [Fact]
    public async Task RunningGameOrSteamUpdatePreventsAllGameWrites()
    {
        string game = Game();
        ModWorkspace workspace = Workspace(static _ => throw new IOException("Game busy"));
        await workspace.CreateProfileAsync("Pending", CancellationToken.None);
        await InstallAsync(workspace, "1", "Skin");
        _ = await Assert.ThrowsAsync<IOException>(() => workspace.ApplyAsync(game, CancellationToken.None));
        Assert.Equal(GameInfo, await File.ReadAllTextAsync(Config(game)));
        Assert.False(Directory.Exists(Owned(game)));
    }

    [Fact]
    public async Task ChangedInstalledFilesAndUnmanagedFoldersAreNeverDeleted()
    {
        string game = Game();
        ModWorkspace workspace = Workspace();
        await workspace.CreateProfileAsync("Modded", CancellationToken.None);
        await InstallAsync(workspace, "1", "Skin");
        await workspace.ApplyAsync(game, CancellationToken.None);
        string installed = Path.Combine(Owned(game), "pak01_dir.vpk");
        await File.AppendAllTextAsync(installed, "foreign edit");
        await workspace.SelectProfileAsync(Guid.Empty, CancellationToken.None);
        _ = await Assert.ThrowsAsync<IOException>(() => workspace.ApplyAsync(game, CancellationToken.None));
        Assert.EndsWith("foreign edit", await File.ReadAllTextAsync(installed));
        Directory.Delete(Owned(game), true);
        _ = Directory.CreateDirectory(Owned(game));
        string foreign = Path.Combine(Owned(game), "manual.txt");
        await File.WriteAllTextAsync(foreign, "keep");
        _ = await Assert.ThrowsAsync<IOException>(() => workspace.ApplyAsync(game, CancellationToken.None));
        Assert.Equal("keep", await File.ReadAllTextAsync(foreign));
    }

    [Fact]
    public async Task InterruptedFirstDeploymentRollsBackAndPreservesLaterSteamChanges()
    {
        string game = Game();
        ModWorkspace workspace = Workspace();
        await workspace.CreateProfileAsync("Modded", CancellationToken.None);
        await InstallAsync(workspace, "1", "Skin");
        await workspace.ApplyAsync(game, CancellationToken.None);
        await File.AppendAllTextAsync(Config(game), "// Steam updated this file\r\n");
        string root = Path.Combine(_root, "Library");
        var journal = new DeploymentJournal(game, Guid.NewGuid().ToString("N"),
            Encoding.UTF8.GetBytes(GameInfo), false);
        ModFiles.WriteJson(Path.Combine(root, "deployment.json"), journal, ModJsonContext.Default.DeploymentJournal);
        new ModDeployment(root, static _ => { }).Recover();
        Assert.False(Directory.Exists(Owned(game)));
        Assert.Equal(GameInfo + "// Steam updated this file\r\n", await File.ReadAllTextAsync(Config(game)));
        Assert.False(File.Exists(Path.Combine(root, "deployment.json")));
    }

    [Fact]
    public async Task CacheCorruptionStopsDeploymentBeforeConfigurationChanges()
    {
        string game = Game();
        ModWorkspace workspace = Workspace();
        await workspace.CreateProfileAsync("Modded", CancellationToken.None);
        await InstallAsync(workspace, "1", "Skin");
        InstalledMod package = workspace.Read().Packages.Single();
        await File.AppendAllTextAsync(Path.Combine(_root, "Library", "Packages", package.Id.ToString("N"), "skin.vpk"),
            "changed");
        _ = await Assert.ThrowsAsync<IOException>(() => workspace.ApplyAsync(game, CancellationToken.None));
        Assert.Equal(GameInfo, await File.ReadAllTextAsync(Config(game)));
        Assert.False(Directory.Exists(Owned(game)));
    }

    [Theory]
    [InlineData("../escape.vpk")]
    [InlineData("/absolute.vpk")]
    [InlineData("folder/../../escape.vpk")]
    [InlineData("C:\\escape.vpk")]
    public async Task UnsafeArchivePathsAreRejected(string entry)
    {
        _ = Directory.CreateDirectory(_root);
        string zip = Path.Combine(_root, "bad.zip");
        await using (ZipArchive archive = await ZipFile.OpenAsync(zip, ZipArchiveMode.Create))
        {
            await using Stream target = await archive.CreateEntry(entry).OpenAsync();
            await target.WriteAsync(Vpk("test"));
        }

        _ = await Assert.ThrowsAsync<IOException>(() => ModPackageExtractor.ExtractAsync(zip,
            Path.Combine(_root, "Extracted"), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_root, "escape.vpk")));
    }

    [Fact]
    public async Task ArchiveVariantsStaySeparateAndExecutablesAreNotExtracted()
    {
        _ = Directory.CreateDirectory(_root);
        string zip = Path.Combine(_root, "variants.zip");
        await using (ZipArchive archive = await ZipFile.OpenAsync(zip, ZipArchiveMode.Create))
        {
            foreach (string name in new[] { "Red/skin.vpk", "Blue/skin.vpk", "setup.exe" })
            {
                await using Stream target = await archive.CreateEntry(name).OpenAsync();
                await target.WriteAsync(Vpk("test"));
            }
        }

        string output = Path.Combine(_root, "Extracted");
        string[] roots = await ModPackageExtractor.ExtractAsync(zip, output, CancellationToken.None);
        Assert.Equal(Expected, roots);
        Assert.False(File.Exists(Path.Combine(output, "setup.exe")));
        _ = Assert.Single(VpkReader.Read(Path.Combine(output, roots[0])).Resources);
    }

    [Fact]
    public void MissingMultipartDataAndTruncatedVpkAreRejected()
    {
        _ = Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "skin_dir.vpk");
        File.WriteAllBytes(path, Vpk("test", 0));
        _ = Assert.Throws<IOException>(() => VpkReader.Read(path));
        File.WriteAllBytes(Path.Combine(_root, "skin_000.vpk"), [42]);
        _ = Assert.Single(VpkReader.Read(path).Parts);
        File.WriteAllBytes(path, Vpk("test")[..^1]);
        _ = Assert.Throws<IOException>(() => VpkReader.Read(path));
    }

    [Theory]
    [InlineData("README.txt")]
    [InlineData("readme.TXT")]
    public void RepeatedRootDocumentationDoesNotInvalidateVpk(string repeated)
    {
        _ = Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "skin.vpk");
        File.WriteAllBytes(path, VpkEntries(("README.txt", 0x7FFF, 0), (repeated, 0x7FFF, 0),
            ("materials/skin.vmat_c", 0x7FFF, 1)));
        VpkContents contents = VpkReader.Read(path);
        Assert.Equal(2, contents.Resources.Length);
        Assert.Contains("materials/skin.vmat_c", contents.Resources);
    }

    [Fact]
    public void RepeatedGameResourcesAreStillRejected()
    {
        _ = Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "skin.vpk");
        File.WriteAllBytes(path, VpkEntries(("materials/skin.vmat_c", 0x7FFF, 1),
            ("materials/SKIN.vmat_c", 0x7FFF, 1)));
        IOException error = Assert.Throws<IOException>(() => VpkReader.Read(path));
        Assert.Contains("Duplicate resource", error.Message);
    }

    [Fact]
    public void RepeatedDocumentationStillRequiresValidDataBounds()
    {
        _ = Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "skin.vpk");
        File.WriteAllBytes(path, VpkEntries(("README.txt", 0x7FFF, 0), ("README.txt", 0x7FFF, 2)));
        IOException error = Assert.Throws<IOException>(() => VpkReader.Read(path));
        Assert.Contains("Missing or incomplete VPK data", error.Message);
    }

    [Fact]
    public async Task SharedRootDocumentationDoesNotConflictBetweenMods()
    {
        string game = Game();
        ModWorkspace workspace = Workspace();
        await workspace.CreateProfileAsync("Skins", CancellationToken.None);
        await InstallAsync(workspace, "1", "First", "first", true);
        await InstallAsync(workspace, "2", "Second", "second", true);
        await workspace.ApplyAsync(game, CancellationToken.None);
        Assert.Equal(2, Directory.GetFiles(Owned(game), "*.vpk").Length);
        await workspace.SelectProfileAsync(Guid.Empty, CancellationToken.None);
        await workspace.ApplyAsync(game, CancellationToken.None);
        Assert.Equal(GameInfo, await File.ReadAllTextAsync(Config(game)));
    }

    [Fact]
    public void CatalogSearchFiltersAndSortingComposeBeforePagination()
    {
        CatalogMod[] mods =
        [
            new() { Name = "Blue Ivy", Author = "Alice", Hero = "Ivy", Category = "Skins", DownloadCount = 10 },
            new() { Name = "Red Ivy", Author = "Bob", Hero = "Ivy", Category = "Skins", DownloadCount = 20 },
            new() { Name = "Ivy sound", Hero = "Ivy", Category = "Sounds" },
            new() { Name = "Adult Ivy", Hero = "Ivy", Category = "Skins", IsNsfw = true }
        ];
        CatalogMod[] result = new ModQuery("ivy", "Skins", "Ivy", 1).Filter(mods);
        Assert.Equal(["Red Ivy", "Blue Ivy"], result.Select(static mod => mod.Name));
        _ = Assert.Single(new ModQuery("alice").Filter(mods));
        Assert.Equal(4, new ModQuery(IncludeNsfw: true).Filter(mods).Length);
    }

    [Fact]
    public void RequiredDependenciesIgnoreInformationalAndOptionalEntries()
    {
        var mod = new CatalogMod
        {
            Dependencies =
            [
                new ModDependency("Own Deadlock", null, null),
                new ModDependency("Required", "42", "required"),
                new ModDependency("Optional", "43", "optional")
            ]
        };
        Assert.Equal("42", Assert.Single(ModRequirements.Required(mod)).RemoteId);
        _ = Assert.Throws<IOException>(() => ModRequirements.Validate(ModLibrary.Empty, Guid.Empty, mod));
        var package = new InstalledMod(Guid.NewGuid(), "42", "Required", "Author", "file.zip",
            DateTimeOffset.UtcNow, []);
        var profile = new ModProfile(Guid.NewGuid(), "Ready", [new ProfileMod(package.Id)]);
        ModRequirements.Validate(new ModLibrary(profile.Id, [ModProfile.Vanilla, profile], [package]), profile.Id, mod);
    }

    [Fact]
    public async Task OlderPackageMetadataWithoutDependencyFieldStillApplies()
    {
        string game = Game();
        ModWorkspace workspace = Workspace();
        await workspace.CreateProfileAsync("Existing", CancellationToken.None);
        await InstallAsync(workspace, "1", "Skin");
        string path = Path.Combine(_root, "Library", "library.json");
        var library = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        _ = library["packages"]![0]!.AsObject().Remove("requiredModIds");
        await File.WriteAllTextAsync(path, library.ToJsonString());
        Assert.Null(workspace.Read().Packages.Single().RequiredModIds);
        await workspace.ApplyAsync(game, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(Owned(game), "pak01_dir.vpk")));
        await workspace.SelectProfileAsync(Guid.Empty, CancellationToken.None);
        await workspace.ApplyAsync(game, CancellationToken.None);
        Assert.Equal(GameInfo, await File.ReadAllTextAsync(Config(game)));
    }

    private ModWorkspace Workspace(Action<string>? ensureIdle = null)
    {
        var workspace = new ModWorkspace(Path.Combine(_root, "Library"), ensureIdle ?? (static _ => { }));
        _workspaces.Add(workspace);
        return workspace;
    }

    private string Game()
    {
        string game = Path.Combine(_root, "Game");
        _ = Directory.CreateDirectory(Path.Combine(game, "game", "bin", "win64"));
        _ = Directory.CreateDirectory(Path.Combine(game, "game", "citadel"));
        File.WriteAllText(Path.Combine(game, "game", "bin", "win64", "deadlock.exe"), "fixture");
        File.WriteAllText(Config(game), GameInfo);
        return game;
    }

    private static string Config(string game)
    {
        return Path.Combine(game, "game", "citadel", "gameinfo.gi");
    }

    private static string Owned(string game)
    {
        return Path.Combine(game, "game", "citadel", "DeadLockyAddons");
    }

    private async Task InstallAsync(ModWorkspace workspace, string remoteId, string name, string resource = "test",
        bool includeDocumentation = false, byte dataValue = 42)
    {
        var id = Guid.NewGuid();
        string content = Path.Combine(_root, "Library", "Downloads", id.ToString("N"), "Content");
        _ = Directory.CreateDirectory(content);
        byte[] bytes = includeDocumentation
            ? VpkEntries(($"materials/{resource}.vmat_c", 0x7FFF, 1), ("README.txt", 0x7FFF, 0))
            : Vpk(resource);
        bytes[^1] = dataValue;
        await File.WriteAllBytesAsync(Path.Combine(content, "skin.vpk"), bytes);
        var prepared = new PreparedMod(id, content, ["skin.vpk"]);
        var mod = new CatalogMod { RemoteId = remoteId, Name = name };
        var download = new ModDownload("https://example.com/mod.zip", 0, "mod.zip", null, null);
        await workspace.InstallAsync(workspace.Read().ActiveProfileId, mod, download, prepared, prepared.Roots,
            CancellationToken.None);
        workspace.Discard(prepared);
    }

    private static byte[] Vpk(string resource, ushort archive = 0x7FFF)
    {
        return VpkEntries(($"materials/{resource}.vmat_c", archive, 1));
    }

    private static byte[] VpkEntries(params (string Path, ushort Archive, uint Length)[] entries)
    {
        using var tree = new MemoryStream();
        using (var writer = new BinaryWriter(tree, Encoding.UTF8, true))
        {
            foreach (IGrouping<string, (string Path, ushort Archive, uint Length)> extension in
                     entries.GroupBy(static entry => Path.GetExtension(entry.Path)[1..]))
            {
                writer.Write(Encoding.UTF8.GetBytes(extension.Key + "\0"));
                foreach (IGrouping<string?, (string Path, ushort Archive, uint Length)> directory in
                         extension.GroupBy(static entry => Path.GetDirectoryName(entry.Path)))
                {
                    writer.Write(Encoding.UTF8.GetBytes(
                        (string.IsNullOrEmpty(directory.Key) ? " " : directory.Key) + "\0"));
                    foreach ((string path, ushort archive, uint length) in directory)
                    {
                        writer.Write(Encoding.UTF8.GetBytes(Path.GetFileNameWithoutExtension(path) + "\0"));
                        writer.Write(0u);
                        writer.Write((ushort)0);
                        writer.Write(archive);
                        writer.Write(0u);
                        writer.Write(length);
                        writer.Write(ushort.MaxValue);
                    }

                    writer.Write((byte)0);
                }

                writer.Write((byte)0);
            }

            writer.Write((byte)0);
        }

        using var result = new MemoryStream();
        using var header = new BinaryWriter(result);
        header.Write(0x55AA1234u);
        header.Write(1u);
        header.Write((uint)tree.Length);
        header.Write(tree.ToArray());
        if (entries.Any(static entry => entry.Archive == 0x7FFF && entry.Length > 0))
        {
            header.Write((byte)42);
        }

        return result.ToArray();
    }
}
