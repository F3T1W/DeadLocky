using System.Buffers.Binary;
using System.Globalization;
using ValveKeyValue;
using ValveKeyValue.KeyValues3;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;

namespace DeadLocky.Features.ManageMods.Api;

internal static class PanoramaImageList
{
    public const string ResourcePath = "panorama/image_compiler.vdata_c";
    private const int MaximumSize = 4 * 1024 * 1024;

    public static byte[] Merge(IEnumerable<VpkEntry> entries, CancellationToken token)
    {
        using var output = new Resource(ResourceType.VData);
        var references = new ResourceExtRefList { Resource = output };
        var referenceNames = new Dictionary<ulong, string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var images = KVObject.Array();
        foreach (VpkEntry entry in entries)
        {
            token.ThrowIfCancellationRequested();
            byte[] bytes = Read(entry);
            ValidateBlocks(bytes);
            using var source = new Resource();
            using var input = new MemoryStream(bytes, false);
            source.Read(input);
            if (source.Version != 0 || source.DataBlock is not BinaryKV3 data
                                    || !data.Data.Root.IsCollection || data.Data.Root.Count != 2
                                    || !data.Data.Root.TryGetValue("generic_data_type", out KVObject? type)
                                    || type.ValueType != KVValueType.String ||
                                    type.ToString(CultureInfo.InvariantCulture) != "panorama_image_list"
                                    || !data.Data.Root.TryGetValue("image_list", out KVObject? list) || !list.IsArray
                                    || list.Count > 20000)
            {
                throw new IOException("The mods use an unsupported Panorama image list. They cannot be merged.");
            }

            foreach (KVObject image in list.Values)
            {
                string name = image.ToString(CultureInfo.InvariantCulture);
                if (image.ValueType != KVValueType.String || image.Flag != KVFlag.Panorama
                                                          || !name.StartsWith("file://{images}/",
                                                              StringComparison.Ordinal) || name.Length > 4096)
                {
                    throw new IOException("The Panorama image list contains an unsupported image reference.");
                }

                if (names.Count >= 20000)
                {
                    throw new IOException("The combined Panorama image list contains too many images.");
                }

                if (names.Add(name))
                {
                    images.Add(image);
                }
            }

            if (source.ExternalReferences is not { } external)
            {
                continue;
            }

            foreach (ResourceExtRefList.ResourceReferenceInfo reference in external.ResourceRefInfoList)
            {
                if (referenceNames.TryAdd(reference.Id, reference.Name))
                {
                    references.ResourceRefInfoList.Add(reference);
                }
                else if (referenceNames[reference.Id] != reference.Name)
                {
                    throw new IOException("The Panorama image lists contain conflicting resource identifiers.");
                }
            }
        }

        var root = KVObject.Collection();
        root.Add("generic_data_type", "panorama_image_list");
        root.Add("image_list", images);
        var block = new BinaryKV3(root,
            new KV3ID("generic", new Guid("7412167c-06e9-4698-aff2-e63eb59037e7")), BlockType.DATA)
        {
            Resource = output
        };
        output.Blocks.Add(references);
        output.Blocks.Add(block);
        using var result = new MemoryStream();
        output.Serialize(result);
        return result.ToArray();
    }

    private static byte[] Read(VpkEntry entry)
    {
        long size = entry.Length + entry.Preload.Length;
        if (size is < 16 or > MaximumSize)
        {
            throw new IOException("The Panorama image list exceeds the supported size limit.");
        }

        ModFiles.RequireRegularPath(entry.DataPath);
        byte[] bytes = new byte[(int)size];
        entry.Preload.CopyTo(bytes, 0);
        using FileStream stream = File.OpenRead(entry.DataPath);
        stream.Position = entry.Offset;
        stream.ReadExactly(bytes.AsSpan(entry.Preload.Length));
        return bytes;
    }

    // Bound compressed KV3 allocations before handing the resource to Source 2 Viewer's parser.
    private static void ValidateBlocks(ReadOnlySpan<byte> bytes)
    {
        long table = 8L + BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        long tableEnd = table + (count * 12L);
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != bytes.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != 12
            || table < 16 || count is 0 or > 8 || tableEnd > bytes.Length)
        {
            throw new IOException("Invalid Panorama image-list header.");
        }

        HashSet<uint> types = [];
        List<(int Offset, int Length)> ranges = [];
        for (int i = 0; i < count; i++)
        {
            int record = (int)table + (i * 12);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes[record..]);
            long offset = record + 4L + BinaryPrimitives.ReadUInt32LittleEndian(bytes[(record + 4)..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(record + 8)..]);
            if (offset < tableEnd || offset + size > bytes.Length || !types.Add(type))
            {
                throw new IOException("Invalid Panorama image-list block.");
            }

            ranges.Add(((int)offset, (int)size));
            ReadOnlySpan<byte> block = bytes.Slice((int)offset, (int)size);
            if (type == 0x4C524552) // RERL
            {
                if (size < 8 || BinaryPrimitives.ReadUInt32LittleEndian(block[4..]) > 20000)
                {
                    throw new IOException("Invalid Panorama image references.");
                }
            }
            else if (type is 0x41544144 or 0x32444552 or 0x49434C46) // DATA, RED2, FLCI
            {
                if (size < 72 || BinaryPrimitives.ReadUInt32LittleEndian(block) is not (0x4B563304 or 0x4B563305)
                              || BinaryPrimitives.ReadUInt32LittleEndian(block[48..]) > MaximumSize
                              || BinaryPrimitives.ReadUInt32LittleEndian(block[56..]) != 0
                              || BinaryPrimitives.ReadUInt32LittleEndian(block[60..]) != 0)
                {
                    throw new IOException("Unsupported or oversized Panorama image-list data.");
                }

                if (BinaryPrimitives.ReadUInt32LittleEndian(block) == 0x4B563305
                    && (size < 120 || (long)BinaryPrimitives.ReadUInt32LittleEndian(block[72..])
                        + BinaryPrimitives.ReadUInt32LittleEndian(block[80..])
                        != BinaryPrimitives.ReadUInt32LittleEndian(block[48..])))
                {
                    throw new IOException("Invalid Panorama image-list buffer sizes.");
                }
            }
            else
            {
                throw new IOException("The Panorama image list contains an unsupported block.");
            }
        }

        long previousEnd = tableEnd;
        foreach ((int offset, int length) in ranges.OrderBy(static range => range.Offset))
        {
            if (offset < previousEnd)
            {
                throw new IOException("Overlapping Panorama image-list blocks.");
            }

            previousEnd = offset + (long)length;
        }
    }
}
