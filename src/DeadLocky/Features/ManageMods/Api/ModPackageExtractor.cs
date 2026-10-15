using System.Text.RegularExpressions;
using SharpCompress.Archives;

namespace DeadLocky.Features.ManageMods.Api;

internal static partial class ModPackageExtractor
{
    public static async Task<string[]> ExtractAsync(string source, string directory, CancellationToken token)
    {
        _ = Directory.CreateDirectory(directory);
        await using (FileStream stream = File.OpenRead(source))
        {
            byte[] signature = new byte[4];
            _ = await stream.ReadAsync(signature, token);
            if (BitConverter.ToUInt32(signature) == 0x55AA1234)
            {
                File.Copy(source, Path.Combine(directory, "mod.vpk"));
                return ["mod.vpk"];
            }
        }

        await using IAsyncArchive archive = await ArchiveFactory.OpenAsyncArchive(source, cancellationToken: token);
        long total = 0;
        int count = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (IArchiveEntry entry in archive.EntriesAsync.WithCancellation(token))
        {
            token.ThrowIfCancellationRequested();
            if (++count > 10000)
            {
                throw new IOException("The archive contains too many entries.");
            }

            if (entry.IsDirectory)
            {
                continue;
            }

            string name = ModFiles.SafeRelativePath(entry.Key ?? "");
            if (!string.IsNullOrEmpty(entry.LinkTarget) || entry.IsEncrypted)
            {
                throw new IOException("Encrypted archives and symbolic links are not supported.");
            }

            if (!name.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!names.Add(name) || entry.Size < 0 || entry.Size > ModFiles.MaximumExtracted - total)
            {
                throw new IOException("The archive contains duplicate files or exceeds the extraction limit.");
            }

            string output = Path.Combine(directory, name);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using Stream input = await entry.OpenEntryStreamAsync(token);
            await using FileStream destination = File.Create(output);
            total += await ModFiles.CopyBoundedAsync(input, destination, ModFiles.MaximumExtracted - total, token);
        }

        string[] roots =
        [
            .. names.Where(static name => !SegmentName().IsMatch(name))
                .Order(StringComparer.Ordinal)
        ];
        if (roots.Length == 0)
        {
            throw new IOException("No installable VPK files were found in this download.");
        }

        return roots;
    }

    [GeneratedRegex(@"_\d{3}\.vpk$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SegmentName();
}
