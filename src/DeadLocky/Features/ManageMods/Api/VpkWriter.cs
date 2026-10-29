using System.IO.Hashing;
using System.Text;

namespace DeadLocky.Features.ManageMods.Api;

internal static class VpkWriter
{
    public static void Replace(string source, string destination, string resource, byte[] replacement,
        CancellationToken token)
    {
        Replace(source, destination, new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [resource] = replacement
        }, token);
    }

    public static void Replace(string source, string destination, IReadOnlyDictionary<string, byte[]> replacements,
        CancellationToken token)
    {
        VpkContents contents = VpkReader.Read(source);
        if (Path.GetFullPath(source) == Path.GetFullPath(destination)
            || replacements.Keys.Any(path => !contents.Entries.ContainsKey(path)))
        {
            throw new IOException("Invalid VPK replacement destination or resource.");
        }

        using var tree = new MemoryStream();
        using var directory = new BinaryWriter(tree, Encoding.UTF8, true);
        List<(VpkEntry Entry, byte[]? Replacement)> payloads = [];
        long offset = 0;
        foreach (IGrouping<string, KeyValuePair<string, VpkEntry>> extension in contents.Entries
                     .GroupBy(static pair => Path.GetExtension(pair.Key)))
        {
            WriteString(extension.Key.Length == 0 ? " " : extension.Key[1..]);
            foreach (IGrouping<string, KeyValuePair<string, VpkEntry>> folder in extension
                         .GroupBy(static pair => Path.GetDirectoryName(pair.Key) ?? ""))
            {
                WriteString(folder.Key.Length == 0 ? " " : folder.Key);
                foreach ((string path, VpkEntry entry) in folder)
                {
                    token.ThrowIfCancellationRequested();
                    bool replaced = replacements.TryGetValue(path, out byte[]? replacement);
                    uint length = replaced ? (uint)replacement!.Length : entry.Length;
                    byte[] preload = replaced ? [] : entry.Preload;
                    if (offset + length > uint.MaxValue)
                    {
                        throw new IOException("The combined VPK exceeds the supported size limit.");
                    }

                    WriteString(Path.GetFileNameWithoutExtension(path));
                    directory.Write(replaced ? Crc32.HashToUInt32(replacement!) : entry.Crc32);
                    directory.Write((ushort)preload.Length);
                    directory.Write((ushort)0x7FFF);
                    directory.Write((uint)offset);
                    directory.Write(length);
                    directory.Write(ushort.MaxValue);
                    directory.Write(preload);
                    offset += length;
                    payloads.Add((entry, replacement));
                }

                directory.Write((byte)0);
            }

            directory.Write((byte)0);
        }

        directory.Write((byte)0);
        using FileStream output = File.Create(destination);
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write(0x55AA1234u);
        writer.Write(1u);
        writer.Write(checked((uint)tree.Length));
        tree.Position = 0;
        tree.CopyTo(output);
        byte[] buffer = new byte[81920];
        foreach ((VpkEntry entry, byte[]? replacement) in payloads)
        {
            token.ThrowIfCancellationRequested();
            if (replacement is not null)
            {
                output.Write(replacement);
                continue;
            }

            ModFiles.RequireRegularPath(entry.DataPath);
            using FileStream input = File.OpenRead(entry.DataPath);
            input.Position = entry.Offset;
            long remaining = entry.Length;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                int count = (int)Math.Min(buffer.Length, remaining);
                input.ReadExactly(buffer.AsSpan(0, count));
                output.Write(buffer.AsSpan(0, count));
                remaining -= count;
            }
        }

        return;

        void WriteString(string value)
        {
            directory.Write(Encoding.UTF8.GetBytes(value));
            directory.Write((byte)0);
        }
    }
}
