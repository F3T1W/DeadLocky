using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DeadLocky.Features.LaunchGame.Api;
using DeadLocky.Features.ManageMods.Api;

namespace DeadLocky.UnitTests;

public sealed class ModResourceConflictTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS()
            ? MacGameProcessScanner.CanonicalPath(Path.GetTempPath())
            : Path.GetTempPath(),
        "DeadLockyResourceHashes-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Theory]
    [InlineData("REDI")]
    [InlineData("RED2")]
    public void TextureBuildProvenanceDoesNotChangeRuntimeFingerprint(string editorBlock)
    {
        byte[] first = Texture("Author A", editorBlock: editorBlock);
        byte[] second = Texture("A different build directory and timestamp", editorBlock: editorBlock);
        Assert.NotEqual(first, second);
        Assert.Equal(Fingerprint(first), Fingerprint(second));
    }

    [Theory]
    [InlineData("DATA")]
    [InlineData("RERL")]
    [InlineData("PIXELS")]
    [InlineData("VERSION")]
    public void TextureRuntimeDifferencesRemainConflicts(string changed)
    {
        byte[] first = Texture("Author A");
        byte[] second = Texture("Author B", changed);
        Assert.NotEqual(Fingerprint(first), Fingerprint(second));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(12)]
    public void InvalidTextureDeclarationsFallBackToExactBytes(int field)
    {
        byte[] first = Texture("Author A");
        byte[] second = Texture("Author B");
        BinaryPrimitives.WriteUInt32LittleEndian(first.AsSpan(field), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(second.AsSpan(field), uint.MaxValue);
        string hash = Fingerprint(first);
        Assert.StartsWith("RAW:", hash);
        Assert.NotEqual(hash, Fingerprint(second));
    }

    [Fact]
    public void NonTextureResourcesAreComparedWithoutDiscardingBlocks()
    {
        byte[] first = Texture("Author A");
        byte[] second = Texture("Author B");
        Assert.NotEqual(Fingerprint(first, "models/hero.vmdl_c"), Fingerprint(second, "models/hero.vmdl_c"));
        Assert.Equal(Fingerprint(first, "models/hero.vmdl_c"), Fingerprint(first, "models/hero.vmdl_c"));
    }

    [Fact]
    public void PreloadAndExternalDataRangeBothContributeToFingerprint()
    {
        _ = Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "data_000.vpk");
        File.WriteAllBytes(path, [90, 91, 3, 4, 92]);
        var entry = new VpkEntry(path, 2, 2, [1, 2]);
        string hash = VpkResourceHash.Create("materials/test.vmat_c", entry, CancellationToken.None);
        Assert.Equal("RAW:" + Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])), hash);
    }

    [Fact]
    public void TruncatedResourceDataIsNeverTreatedAsEquivalent()
    {
        _ = Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "data.vpk");
        File.WriteAllBytes(path, [1]);
        var entry = new VpkEntry(path, 0, 2, []);
        _ = Assert.Throws<EndOfStreamException>(() =>
            VpkResourceHash.Create("materials/test.vmat_c", entry, CancellationToken.None));
    }

    private string Fingerprint(byte[] bytes, string resource = "materials/CancellationToken.None/default_ao.vtex_c")
    {
        _ = Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, bytes);
        return VpkResourceHash.Create(resource, new VpkEntry(path, 0, (uint)bytes.Length, []), CancellationToken.None);
    }

    private static byte[] Texture(string provenance, string changed = "", string editorBlock = "RED2")
    {
        (string Type, byte[] Bytes)[] blocks =
        [
            (editorBlock, Encoding.UTF8.GetBytes(provenance)),
            ("DATA", [1, 2, 3, changed == "DATA" ? (byte)9 : (byte)4]),
            ("RERL", [5, 6, 7, changed == "RERL" ? (byte)9 : (byte)8])
        ];
        int size = 16 + (blocks.Length * 12) + blocks.Sum(static block => block.Bytes.Length);
        byte[] bytes = new byte[size + 8];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, size);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), changed == "VERSION" ? (ushort)2 : (ushort)1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), blocks.Length);
        int offset = 16 + (blocks.Length * 12);
        for (int index = 0; index < blocks.Length; index++)
        {
            int record = 16 + (index * 12);
            Encoding.ASCII.GetBytes(blocks[index].Type).CopyTo(bytes, record);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(record + 4), offset - record - 4);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(record + 8), blocks[index].Bytes.Length);
            blocks[index].Bytes.CopyTo(bytes, offset);
            offset += blocks[index].Bytes.Length;
        }

        bytes[^1] = changed == "PIXELS" ? (byte)9 : (byte)8;
        return bytes;
    }
}
