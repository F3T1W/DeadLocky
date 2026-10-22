using System.Text.Json;
using System.Text.Json.Serialization;
using DeadLocky.Entities.Game.Model;
using DeadLocky.Shared.Api.Diagnostics;

namespace DeadLocky.Features.PrepareGame.Model;

internal sealed record InstalledFile(string Path, long Size, long ModifiedTicks);

internal sealed record InstallationReceipt(uint AppId, string Version, List<InstalledFile?> Files);

internal sealed record LauncherSettings(string Directory);

[JsonSerializable(typeof(InstallationReceipt))]
[JsonSerializable(typeof(LauncherSettings))]
internal partial class LauncherJsonContext : JsonSerializerContext;

internal sealed class InstallationStore(string settingsPath)
{
    public const uint AppId = 1422450;

    public GameDirectory ReadDirectory()
    {
        try
        {
            LauncherSettings? settings = JsonSerializer.Deserialize(File.ReadAllText(settingsPath),
                LauncherJsonContext.Default.LauncherSettings);
            if (settings is not null && !string.IsNullOrWhiteSpace(settings.Directory)
                                     && Path.IsPathFullyQualified(settings.Directory))
            {
                return new GameDirectory(settings.Directory);
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Event("installation.configuration", "failed", exception);
        }

        return new GameDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games", "Deadlock"));
    }

    public void SaveDirectory(GameDirectory directory)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        WriteAtomic(settingsPath, JsonSerializer.Serialize(new LauncherSettings(directory.Path),
            LauncherJsonContext.Default.LauncherSettings));
    }

    public static Task<bool> IsDownloadedAsync(GameDirectory directory, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            try
            {
                InstallationReceipt? receipt = JsonSerializer.Deserialize(File.ReadAllText(ReceiptPath(directory.Path)),
                    LauncherJsonContext.Default.InstallationReceipt);
                if (receipt is not { AppId: AppId, Files.Count: > 0 })
                {
                    return false;
                }

                foreach (InstalledFile? file in receipt.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (file is null || string.IsNullOrWhiteSpace(file.Path) || file.Size < 0)
                    {
                        return false;
                    }

                    var info = new FileInfo(SafePath(directory.Path, file.Path));
                    if (!info.Exists || info.Length != file.Size || info.LastWriteTimeUtc.Ticks != file.ModifiedTicks)
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Exception exception) when (exception is IOException or JsonException
                                                  or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }, cancellationToken);
    }

    private static string SafePath(string root, string relativePath)
    {
        root = Path.GetFullPath(root);
        string relative = relativePath.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || Path.IsPathRooted(relative)
            || relative.Split('/').Any(static segment => segment is ".." or "." or ""))
        {
            throw new IOException("Steam returned an unsafe file path.");
        }

        string destination = Path.GetFullPath(Path.Combine(root, relative));
        if (!destination.StartsWith(root.TrimEnd('/') + '/', StringComparison.Ordinal))
        {
            throw new IOException("The game file is outside the install folder.");
        }

        RejectLinks(destination);
        return destination;
    }

    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path);
             current is not null;
             current = Path.GetDirectoryName(current))
        {
            var info = new FileInfo(current);
            if (info.LinkTarget is not null)
            {
                throw new IOException("Choose an install folder without symbolic links.");
            }
        }
    }

    public static string ReceiptPath(string directory)
    {
        return SafePath(directory, ".deadlocky/installation.json");
    }

    private static void WriteAtomic(string path, string contents)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, contents);
            File.Move(temporary, path, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
