using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using DeadLocky.Features.LaunchGame.Api;
using DeadLocky.Features.ManageMods.Api;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using KV3ID = ValveKeyValue.KeyValues3.KV3ID;

namespace DeadLocky.UnitTests;

public sealed class PanoramaImageListTests : IDisposable
{
    private static readonly string[] SourceArray = ["qol.png", "shared.png", "fem.png"];

    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS()
            ? MacGameProcessScanner.CanonicalPath(Path.GetTempPath())
            : Path.GetTempPath(),
        "DeadLockyPanorama-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Theory]
    [InlineData(KV3BinaryCompressionMethod.Uncompressed)]
    [InlineData(KV3BinaryCompressionMethod.Lz4)]
    [InlineData(KV3BinaryCompressionMethod.Zstd)]
    public void MergeKeepsImagesAndExternalReferencesAndDeduplicatesSharedEntries(
        KV3BinaryCompressionMethod compression)
    {
        VpkEntry first = Entry(ImageList(compression, "qol.png", "shared.png"));
        VpkEntry second = Entry(ImageList(compression, "fem.png", "shared.png"));
        byte[] merged = PanoramaImageList.Merge([first, second], CancellationToken.None);
        using var resource = new Resource();
        using var input = new MemoryStream(merged);
        resource.Read(input);
        BinaryKV3 data = Assert.IsType<BinaryKV3>(resource.DataBlock, false);
        Assert.Equal(SourceArray.Select(ImageName),
            data.Data.Root["image_list"].Values.Select(static image => image.ToString(CultureInfo.InvariantCulture)));
        Assert.All(data.Data.Root["image_list"].Values, static image => Assert.Equal(KVFlag.Panorama, image.Flag));
        Assert.Equal(3, Assert.IsType<ResourceExtRefList>(resource.ExternalReferences).ResourceRefInfoList.Count);
    }

    [Fact]
    public void UnsupportedListsAndOversizedCompressedBuffersAreRejected()
    {
        byte[] unsupported = ImageList(KV3BinaryCompressionMethod.Uncompressed, "a.png");
        const int dataRecord = 28;
        int data = dataRecord + 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(unsupported.AsSpan(dataRecord + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(unsupported.AsSpan(data + 48), uint.MaxValue);
        _ = Assert.Throws<IOException>(() => PanoramaImageList.Merge([Entry(unsupported)], CancellationToken.None));
    }

    [Fact]
    public void ConflictingExternalResourceIdsAreRejected()
    {
        byte[] first = ImageList(KV3BinaryCompressionMethod.Uncompressed, "first.png");
        byte[] second = ImageList(KV3BinaryCompressionMethod.Uncompressed, "second.png");
        int firstRerl = 20 + (int)BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(20));
        int secondRerl = 20 + (int)BinaryPrimitives.ReadUInt32LittleEndian(second.AsSpan(20));
        first.AsSpan(firstRerl + 8, 8).CopyTo(second.AsSpan(secondRerl + 8));
        _ = Assert.Throws<IOException>(() =>
            PanoramaImageList.Merge([Entry(first), Entry(second)], CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepackingPreservesOtherResourcesPreloadAndCrcWithoutChangingOriginal(bool multipart)
    {
        _ = Directory.CreateDirectory(_root);
        byte[] original = Archive(multipart);
        string source = Path.Combine(_root, "pak01_dir.vpk");
        File.WriteAllBytes(source, original);
        if (multipart)
        {
            File.WriteAllBytes(Path.Combine(_root, "pak01_000.vpk"), [3, 4]);
        }

        string destination = Path.Combine(_root, "result.vpk");
        byte[] replacement = ImageList(KV3BinaryCompressionMethod.Uncompressed, "qol.png", "fem.png");
        VpkWriter.Replace(source, destination, PanoramaImageList.ResourcePath, replacement, CancellationToken.None);
        VpkContents result = VpkReader.Read(destination);
        Assert.Empty(result.Parts);
        VpkEntry image = result.Entries[PanoramaImageList.ResourcePath];
        Assert.Equal(Crc32.HashToUInt32(replacement), image.Crc32);
        Assert.Equal("RAW:" + Convert.ToHexString(SHA256.HashData(replacement)),
            VpkResourceHash.Create(PanoramaImageList.ResourcePath, image, CancellationToken.None));
        VpkEntry other = result.Entries["materials/keep.vmat_c"];
        Assert.Equal(new byte[] { 1, 2 }, other.Preload);
        Assert.Equal(Crc32.HashToUInt32([1, 2, 3, 4]), other.Crc32);
        Assert.Equal(VpkResourceHash.Create("materials/keep.vmat_c",
                VpkReader.Read(source).Entries["materials/keep.vmat_c"], CancellationToken.None),
            VpkResourceHash.Create("materials/keep.vmat_c", other, CancellationToken.None));
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Fact]
    public void ImageListMergingDoesNotSuppressUnrelatedConflicts()
    {
        _ = Directory.CreateDirectory(_root);
        string first = Path.Combine(_root, "first.vpk");
        string second = Path.Combine(_root, "second.vpk");
        File.WriteAllBytes(first, Archive(false));
        byte[] different = Archive(false);
        different[^1]++;
        File.WriteAllBytes(second, different);
        var conflicts = new ModResourceConflicts(CancellationToken.None, true);
        VpkContents contents = VpkReader.Read(first);
        Assert.Null(conflicts.Add("First", first, contents.Resources));
        Assert.Equal("materials/keep.vmat_c", conflicts.Add("Second", second, contents.Resources)?.Resource);
    }

    private VpkEntry Entry(byte[] bytes)
    {
        _ = Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, bytes);
        return new VpkEntry(path, 0, (uint)bytes.Length, []);
    }

    private static string ImageName(string name)
    {
        return "file://{images}/" + name;
    }

    private static byte[] ImageList(KV3BinaryCompressionMethod compression, params string[] names)
    {
        using var resource = new Resource(ResourceType.VData);
        var references = new ResourceExtRefList { Resource = resource };
        var images = KVObject.Array();
        foreach (string name in names)
        {
            images.Add(new KVObject(ImageName(name)) { Flag = KVFlag.Panorama });
            references.ResourceRefInfoList.Add(new ResourceExtRefList.ResourceReferenceInfo
            {
                Id = Crc32.HashToUInt32(Encoding.UTF8.GetBytes(name)),
                Name = "panorama/images/" + name + ".vtex"
            });
        }

        var root = KVObject.Collection();
        root.Add("generic_data_type", "panorama_image_list");
        root.Add("image_list", images);
        var data = new BinaryKV3(root,
            new KV3ID("generic", new Guid("7412167c-06e9-4698-aff2-e63eb59037e7")), BlockType.DATA)
        {
            Resource = resource,
            SerializationCompressionMethod = compression
        };
        resource.Blocks.Add(references);
        resource.Blocks.Add(data);
        using var stream = new MemoryStream();
        resource.Serialize(stream);
        return stream.ToArray();
    }

    private static byte[] Archive(bool multipart)
    {
        byte[] image = ImageList(KV3BinaryCompressionMethod.Lz4, "original.png");
        using var tree = new MemoryStream();
        using (var writer = new BinaryWriter(tree, Encoding.UTF8, true))
        {
            writer.Write("vdata_c\0panorama\0image_compiler\0"u8);
            writer.Write(Crc32.HashToUInt32(image));
            writer.Write((ushort)0);
            writer.Write((ushort)0x7FFF);
            writer.Write(0u);
            writer.Write((uint)image.Length);
            writer.Write(ushort.MaxValue);
            writer.Write(new byte[2]);
            writer.Write("vmat_c\0materials\0keep\0"u8);
            writer.Write(Crc32.HashToUInt32([1, 2, 3, 4]));
            writer.Write((ushort)2);
            writer.Write(multipart ? (ushort)0 : (ushort)0x7FFF);
            writer.Write(multipart ? 0u : (uint)image.Length);
            writer.Write(2u);
            writer.Write(ushort.MaxValue);
            writer.Write([1, 2]);
            writer.Write(new byte[3]);
        }

        using var output = new MemoryStream();
        using var header = new BinaryWriter(output);
        header.Write(0x55AA1234u);
        header.Write(1u);
        header.Write((uint)tree.Length);
        header.Write(tree.ToArray());
        header.Write(image);
        if (!multipart)
        {
            header.Write([3, 4]);
        }

        return output.ToArray();
    }
}
