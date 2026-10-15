using System.Text;
using DeadLocky.Features.ManageMods.Model;

namespace DeadLocky.Features.ManageMods.Api;

internal sealed record VpkContents(string[] Resources, string[] Parts, Dictionary<string, VpkEntry> Entries);

internal sealed record VpkEntry(string DataPath, long Offset, uint Length, byte[] Preload, uint Crc32 = 0);

internal static class VpkReader
{
    public static VpkContents Read(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (stream.Length < 12 || reader.ReadUInt32() != 0x55AA1234)
        {
            throw new IOException($"Not a VPK archive: {Path.GetFileName(path)}");
        }

        uint version = reader.ReadUInt32();
        uint treeSize = reader.ReadUInt32();
        int headerSize = version switch { 1 => 12, 2 => 28, _ => throw new IOException("Unsupported VPK version.") };
        long end = headerSize + treeSize;
        if (treeSize is 0 or > 32 * 1024 * 1024 || end > stream.Length)
        {
            throw new IOException("The VPK directory is truncated or too large.");
        }

        stream.Position = headerSize;
        var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new Dictionary<string, VpkEntry>(StringComparer.OrdinalIgnoreCase);
        int entryCount = 0;
        while (ReadString() is { Length: > 0 } extension)
        {
            while (ReadString() is { Length: > 0 } directory)
            {
                while (ReadString() is { Length: > 0 } name)
                {
                    if (stream.Position + 18 > end || ++entryCount > 250000)
                    {
                        throw new IOException("Invalid VPK file directory.");
                    }

                    uint crc = reader.ReadUInt32();
                    ushort preload = reader.ReadUInt16();
                    ushort archive = reader.ReadUInt16();
                    uint offset = reader.ReadUInt32();
                    uint length = reader.ReadUInt32();
                    if (reader.ReadUInt16() != ushort.MaxValue || stream.Position + preload > end)
                    {
                        throw new IOException("Invalid VPK entry.");
                    }

                    byte[] preloaded = reader.ReadBytes(preload);
                    string resource = ModFiles.SafeRelativePath((directory == " " ? "" : directory + "/")
                                                                + name + (extension == " " ? "" : "." + extension));
                    if (!resources.Add(resource) && ModResourcePaths.IsGameContent(resource))
                    {
                        throw new IOException($"Duplicate resource in VPK: {resource}");
                    }

                    string data = path;
                    long dataOffset = end;
                    if (archive != 0x7FFF)
                    {
                        if (!path.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new IOException("A multipart VPK must use a _dir.vpk index.");
                        }

                        data = path[..^8] + $"_{archive:000}.vpk";
                        _ = parts.Add(data);
                        dataOffset = 0;
                    }

                    ModFiles.RequireRegularPath(data);
                    if (!File.Exists(data) || dataOffset + offset + length > new FileInfo(data).Length)
                    {
                        throw new IOException($"Missing or incomplete VPK data: {Path.GetFileName(data)}");
                    }

                    _ = entries.TryAdd(resource, new VpkEntry(data, dataOffset + offset, length, preloaded, crc));
                }
            }
        }

        if (resources.Count == 0 || stream.Position != end)
        {
            throw new IOException("The VPK contains no resources or an invalid directory terminator.");
        }

        return new VpkContents([.. resources], [.. parts], entries);

        string ReadString()
        {
            List<byte> bytes = [];
            while (stream.Position < end && bytes.Count < 4096)
            {
                byte next = reader.ReadByte();
                if (next == 0)
                {
                    return Encoding.UTF8.GetString(bytes.ToArray());
                }

                bytes.Add(next);
            }

            throw new IOException("Invalid VPK string.");
        }
    }
}
