using System.IO.Hashing;
using System.Text;
using DeadLocky.Entities.Mod.Model;
using DeadLocky.Features.LaunchGame.Api;
using DeadLocky.Features.ManageMods.Api;
using DeadLocky.Features.ManageMods.Model;

namespace DeadLocky.UnitTests;

public sealed class CameraRepairTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS() ? MacGameProcessScanner.CanonicalPath(Path.GetTempPath()) : Path.GetTempPath(), "DeadLockyCameraTests-" + Guid.NewGuid().ToString("N"));

    public CameraRepairTests()
    {
        _ = Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    [Fact]
    public void MetadataReplacementRetainsOtherSectionsAndQuotedBrackets()
    {
        const string source = "{\n\tOther = { value = \"keep } [ \\\" this\" }\n\tAttachmentCameraData = [ { old = 1 } ]\n}";
        const string donor = "{\n\tAttachmentCameraData = [ { name = \"near_00\" } ]\n\tOther = { value = \"do not copy\" }\n}";
        string output = ModelCameraRepair.ReplaceSection(source, donor, "AttachmentCameraData");
        Assert.Contains("Other = { value = \"keep } [ \\\" this\" }", output);
        Assert.Contains("AttachmentCameraData = [ { name = \"near_00\" } ]", output);
        Assert.DoesNotContain("old =", output);
        Assert.DoesNotContain("do not copy", output);
    }

    [Fact]
    public void MissingCameraMetadataIsAddedOnce()
    {
        const string source = "{\n\tOther = { enabled = true }\n}";
        const string donor = "{\n\tAttachmentCameraData = [ { name = \"near_00\" } ]\n}";
        string first = ModelCameraRepair.ReplaceSection(source, donor, "AttachmentCameraData");
        Assert.Contains("Other = { enabled = true }", first);
        Assert.Equal(first, ModelCameraRepair.ReplaceSection(first, donor, "AttachmentCameraData"));
    }

    [Fact]
    public void TruncatedCameraMetadataIsRejected()
    {
        _ = Assert.Throws<IOException>(static () => ModelCameraRepair.ReplaceSection("{}",
            "{\n\tAttachmentCameraData = [ { name = \"broken\" }", "AttachmentCameraData"));
    }

    [Fact]
    public void MalformedModelIsRejectedBeforeParsing()
    {
        _ = Assert.Throws<IOException>(static () => ModelCameraRepair.Repair(new byte[16], new byte[16], CancellationToken.None));
    }

    [Fact]
    public void RepackingMultipleModelsPreservesOtherResourceBytes()
    {
        string source = Path.Combine(_root, "source.vpk");
        string output = Path.Combine(_root, "output.vpk");
        WriteVpk(source, new Dictionary<string, byte[]>
        {
            ["models/first.vmdl_c"] = [1, 2],
            ["models/second.vmdl_c"] = [3, 4],
            ["materials/retained.vmat_c"] = [5, 6, 7]
        });
        VpkWriter.Replace(source, output, new Dictionary<string, byte[]>
        {
            ["models/first.vmdl_c"] = [8],
            ["models/second.vmdl_c"] = [9, 10, 11]
        }, CancellationToken.None);
        VpkContents contents = VpkReader.Read(output);
        Assert.Equal(3, contents.Entries.Count);
        Assert.Equal([8], Read(contents.Entries["models/first.vmdl_c"]));
        Assert.Equal([9, 10, 11], Read(contents.Entries["models/second.vmdl_c"]));
        Assert.Equal([5, 6, 7], Read(contents.Entries["materials/retained.vmat_c"]));
    }

    [Fact]
    public async Task UnrecognizedModIsNotReplacedAndLeavesNoRepairPackage()
    {
        (Guid id, string game) = SetupLibrary(false);
        byte[] original = await File.ReadAllBytesAsync(Path.Combine(_root, "library.json"));
        await using var workspace = new ModWorkspace(_root, static _ => { });
        CameraRepairResult[] results = await workspace.RepairCamerasAsync(game, [id], CancellationToken.None);
        Assert.False(Assert.Single(results).Repaired);
        Assert.Equal(id, results[0].PackageId);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(_root, "library.json")));
        _ = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "Packages")));
    }

    [Fact]
    public async Task CorruptModelRetainsOriginalAndReportsFailure()
    {
        (Guid id, string game) = SetupLibrary(true);
        await using var workspace = new ModWorkspace(_root, static _ => { });
        CameraRepairResult[] results = await workspace.RepairCamerasAsync(game, [id], CancellationToken.None);
        Assert.False(Assert.Single(results).Repaired);
        Assert.Equal(id, results[0].PackageId);
        Assert.StartsWith("Not repaired:", results[0].Detail);
        Assert.Equal(id, Assert.Single(workspace.Read().ActiveProfile.Mods).PackageId);
        _ = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "Packages")));
    }

    [Fact]
    public async Task GameBusyPreventsRepairAndRestore()
    {
        (Guid id, string game) = SetupLibrary(false);
        await using var workspace = new ModWorkspace(_root, static _ => throw new IOException("Game running"));
        _ = await Assert.ThrowsAsync<IOException>(() => workspace.RepairCamerasAsync(game, [id], CancellationToken.None));
        _ = await Assert.ThrowsAsync<IOException>(() => workspace.RestoreCameraOriginalsAsync(game, [id], CancellationToken.None));
    }

    [Fact]
    public async Task SelectionMustBelongToCurrentProfile()
    {
        (_, string game) = SetupLibrary(false);
        await using var workspace = new ModWorkspace(_root, static _ => { });
        _ = await Assert.ThrowsAsync<IOException>(() => workspace.RepairCamerasAsync(game, [Guid.NewGuid()], CancellationToken.None));
        _ = await Assert.ThrowsAsync<IOException>(() => workspace.RepairCamerasAsync(game, [], CancellationToken.None));
    }

    [Fact]
    public async Task RestoreOriginalPreservesProfileEnableStateAndOtherMods()
    {
        (Guid original, string game) = SetupLibrary(false);
        await using var workspace = new ModWorkspace(_root, static _ => { });
        ModLibrary library = workspace.Read();
        var repaired = Guid.NewGuid();
        InstalledMod copy = library.Packages[0] with { Id = repaired, CameraRepairOriginalPackageId = original };
        ModProfile profile = library.ActiveProfile with { Mods = [new ProfileMod(repaired, false)] };
        var anotherProfile = Guid.NewGuid();
        SaveLibrary(library with
        {
            Packages = [.. library.Packages, copy],
            Profiles = [ModProfile.Vanilla, profile, new ModProfile(anotherProfile, "Other", [new ProfileMod(repaired)])]
        });
        await workspace.RestoreCameraOriginalsAsync(game, [repaired], CancellationToken.None);
        ModLibrary restored = workspace.Read();
        Assert.Equal(new ProfileMod(original, false), Assert.Single(restored.ActiveProfile.Mods));
        Assert.Equal(new ProfileMod(original), Assert.Single(restored.Profiles.Single(item => item.Id == anotherProfile).Mods));
        Assert.Equal(2, restored.Packages.Length);
    }

    private (Guid Id, string Game) SetupLibrary(bool corruptModel)
    {
        var id = Guid.NewGuid();
        string directory = Path.Combine(_root, "Packages", id.ToString("N"));
        _ = Directory.CreateDirectory(directory);
        string vpk = Path.Combine(directory, "pak01_dir.vpk");
        string resource = corruptModel ? "models/hero.vmdl_c" : "materials/hero.vmat_c";
        WriteVpk(vpk, new Dictionary<string, byte[]> { [resource] = [1, 2, 3] });
        string game = Path.Combine(_root, "Game");
        string citadel = Path.Combine(game, "game", "citadel");
        _ = Directory.CreateDirectory(citadel);
        WriteVpk(Path.Combine(citadel, "pak01_dir.vpk"), new Dictionary<string, byte[]> { [resource] = [4, 5, 6] });
        var file = new ModPackageFile("pak01_dir.vpk", ModFiles.Hash(vpk), [resource], []);
        var package = new InstalledMod(id, "123", "Test mod", "Author", "test.zip", DateTimeOffset.UtcNow, [file]);
        var profile = new ModProfile(Guid.NewGuid(), "My profile", [new ProfileMod(id)]);
        SaveLibrary(new ModLibrary(profile.Id, [ModProfile.Vanilla, profile], [package]));
        return (id, game);
    }

    private void SaveLibrary(ModLibrary library)
    {
        ModFiles.WriteJson(Path.Combine(_root, "library.json"), library, ModJsonContext.Default.ModLibrary);
    }

    private static void WriteVpk(string path, Dictionary<string, byte[]> resources)
    {
        using var tree = new MemoryStream();
        using var writer = new BinaryWriter(tree, Encoding.UTF8, true);
        uint offset = 0;
        List<byte[]> payloads = [];
        foreach (IGrouping<string, KeyValuePair<string, byte[]>> group in resources.GroupBy(static pair => Path.GetExtension(pair.Key)[1..]))
        {
            WriteString(group.Key);
            foreach (IGrouping<string, KeyValuePair<string, byte[]>> folder in group.GroupBy(static pair => Path.GetDirectoryName(pair.Key)!))
            {
                WriteString(folder.Key);
                foreach ((string name, byte[] data) in folder)
                {
                    WriteString(Path.GetFileNameWithoutExtension(name));
                    writer.Write(Crc32.HashToUInt32(data));
                    writer.Write((ushort)0);
                    writer.Write((ushort)0x7FFF);
                    writer.Write(offset);
                    writer.Write((uint)data.Length);
                    writer.Write(ushort.MaxValue);
                    offset += (uint)data.Length;
                    payloads.Add(data);
                }

                WriteString("");
            }

            WriteString("");
        }

        WriteString("");
        using FileStream output = File.Create(path);
        using var header = new BinaryWriter(output, Encoding.UTF8, true);
        header.Write(0x55AA1234u);
        header.Write(1u);
        header.Write((uint)tree.Length);
        tree.Position = 0;
        tree.CopyTo(output);
        foreach (byte[] payload in payloads) { output.Write(payload); }
        return;

        void WriteString(string text)
        {
            writer.Write(Encoding.UTF8.GetBytes(text));
            writer.Write((byte)0);
        }
    }

    private static byte[] Read(VpkEntry entry)
    {
        byte[] bytes = new byte[entry.Length];
        using FileStream stream = File.OpenRead(entry.DataPath);
        stream.Position = entry.Offset;
        stream.ReadExactly(bytes);
        Assert.Equal(entry.Crc32, Crc32.HashToUInt32(bytes));
        return bytes;
    }
}
