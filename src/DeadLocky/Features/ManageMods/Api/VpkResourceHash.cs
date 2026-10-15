using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DeadLocky.Features.ManageMods.Api;

internal static class VpkResourceHash
{
    private const int MaximumTextureSize = 32 * 1024 * 1024;

    public static string Create(string resource, VpkEntry entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ModFiles.RequireRegularPath(entry.DataPath);
        using FileStream stream = File.OpenRead(entry.DataPath);
        stream.Position = entry.Offset;
        long size = entry.Preload.Length + entry.Length;
        if (resource.EndsWith(".vtex_c", StringComparison.OrdinalIgnoreCase) && size <= MaximumTextureSize)
        {
            byte[] bytes = new byte[(int)size];
            entry.Preload.CopyTo(bytes, 0);
            stream.ReadExactly(bytes.AsSpan(entry.Preload.Length));
            return TextureFingerprint(bytes) ?? ("RAW:" + Convert.ToHexString(SHA256.HashData(bytes)));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(entry.Preload);
        byte[] buffer = new byte[81920];
        long remaining = entry.Length;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, remaining);
            stream.ReadExactly(buffer.AsSpan(0, count));
            hash.AppendData(buffer.AsSpan(0, count));
            remaining -= count;
        }

        return "RAW:" + Convert.ToHexString(hash.GetHashAndReset());
    }

    // Source 2 layout: https://s2v.app/ValveResourceFormat/guides/read-resource.html
    private static string? TextureFingerprint(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != 12)
        {
            return null;
        }

        long size = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        long table = 8L + BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        long tableEnd = table + (count * 12L);
        if (size > bytes.Length || table < 16 || count is 0 or > 64 || tableEnd > size)
        {
            return null;
        }

        List<(uint Type, int Offset, int Size)> blocks = [];
        HashSet<uint> types = [];
        for (int index = 0; index < count; index++)
        {
            int record = (int)table + (index * 12);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes[record..]);
            long offset = record + 4L + BinaryPrimitives.ReadUInt32LittleEndian(bytes[(record + 4)..]);
            long length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(record + 8)..]);
            if (offset < tableEnd || offset + length > size || !types.Add(type))
            {
                return null;
            }

            blocks.Add((type, (int)offset, (int)length));
        }

        long previousEnd = tableEnd;
        foreach ((_, int offset, int length) in blocks.OrderBy(static block => block.Offset))
        {
            if (offset < previousEnd)
            {
                return null;
            }

            previousEnd = offset + (long)length;
        }

        if (!types.Contains(0x41544144)) // DATA
        {
            return null;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes.Slice(4, 4)); // Header and resource versions remain significant.
        Span<byte> blockHeader = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(blockHeader,
            blocks.Count(static block => block.Type is not (0x49444552 or 0x32444552)));
        hash.AppendData(blockHeader[..4]);
        foreach ((uint type, int offset, int length) in blocks)
        {
            if (type is 0x49444552 or 0x32444552) // REDI/RED2: editor and compiler provenance.
            {
                continue;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(blockHeader, type);
            BinaryPrimitives.WriteInt32LittleEndian(blockHeader[4..], length);
            hash.AppendData(blockHeader);
            hash.AppendData(bytes.Slice(offset, length));
        }

        // Texture pixels may follow the structured resource's declared file size.
        BinaryPrimitives.WriteInt64LittleEndian(blockHeader, bytes.Length - size);
        hash.AppendData(blockHeader);
        hash.AppendData(bytes[(int)size..]);
        return "TEXTURE:" + Convert.ToHexString(hash.GetHashAndReset());
    }
}
