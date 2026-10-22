using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DeadLocky.Shared.Api.Diagnostics;

namespace DeadLocky.Features.ManageMods.Api;

internal static class ModFiles
{
    public const long MaximumDownload = 1024L * 1024 * 1024;
    public const long MaximumExtracted = 4 * MaximumDownload;

    public static void RequireRegularPath(string path)
    {
        for (string? current = Path.GetFullPath(path);
             current is not null;
             current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Symbolic links are not supported for mod storage: {current}");
            }
        }
    }

    public static string SafeRelativePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.Length == 0 || Path.IsPathRooted(normalized) || normalized.Contains(':')
               || normalized.Split('/').Any(static part => part is ".." or "." or "")
            ? throw new IOException("The archive contains an unsafe file path.")
            : normalized;
    }

    public static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static void WriteJson<T>(string path, T value, JsonTypeInfo<T> type)
    {
        AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(value, type));
    }

    public static void AtomicWrite(string path, byte[] bytes)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }

            File.Move(temporary, path, true);
            AppLog.Event("files.atomic-write", "committed");
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static async Task<long> CopyBoundedAsync(Stream source, Stream destination, long limit,
        CancellationToken token, IProgress<long>? progress = null)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, token);
            if (read == 0)
            {
                return total;
            }

            total = checked(total + read);
            if (total > limit)
            {
                throw new IOException("The mod exceeds the supported size limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), token);
            progress?.Report(total);
        }
    }
}
