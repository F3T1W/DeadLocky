using System.Text.Json;
using System.Text.Json.Serialization;
using DeadLocky.Features.LaunchGame.Model;
using DeadLocky.Shared.Api.Diagnostics;

namespace DeadLocky.Features.LaunchGame.Api;

[JsonSerializable(typeof(GameLaunchConfiguration))]
internal partial class LaunchJsonContext : JsonSerializerContext;

internal sealed class GptkConfigurationStore(string path)
{
    public GameLaunchConfiguration? DiscoverOwnedInstallation()
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach ((string runtime, string prefix) in new[]
                     { ("HighballWine11", "Steam-Wine11"), ("ManagedWine11-r21", "Steam-Managed") })
        {
            if (prefix == "Steam-Managed"
                && !File.Exists(Path.Combine(directory, "Prefixes", prefix, ".deadlocky-ready")))
            {
                continue;
            }

            var configuration = new GameLaunchConfiguration(
                Path.Combine(directory, "Runtime", runtime, "engine", "bin", "wine64"),
                Path.Combine(directory, "Prefixes", prefix));
            try
            {
                Validate(configuration);
                AppLog.Event("runtime.discovery", "validated");
                return configuration;
            }
            catch (IOException)
            {
            }
        }

        return null;
    }

    public GameLaunchConfiguration? Read()
    {
        try
        {
            GameLaunchConfiguration? configuration = JsonSerializer.Deserialize(File.ReadAllText(path),
                LaunchJsonContext.Default.GameLaunchConfiguration);
            if (configuration is not null)
            {
                Validate(configuration);
            }

            AppLog.Event("runtime.configuration.read", configuration is null ? "empty" : "validated");
            return configuration;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Event("runtime.configuration.read", "failed", exception);
            return null;
        }
    }

    public void Save(GameLaunchConfiguration configuration)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(configuration,
                LaunchJsonContext.Default.GameLaunchConfiguration));
            File.Move(temporary, path, true);
            AppLog.Event("runtime.configuration.write", "committed");
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static string SteamExecutable(GameLaunchConfiguration configuration)
    {
        string[] locations = ["Program Files (x86)", "Program Files"];
        return locations.Select(folder => Path.Combine(configuration.PrefixDirectory, "drive_c", folder,
                   "Steam", "steam.exe")).FirstOrDefault(File.Exists)
               ?? throw new IOException(
                   "Windows Steam is not installed in this GPTK prefix. Install Steam there first.");
    }

    public static void Validate(GameLaunchConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.WineExecutable)
            || string.IsNullOrWhiteSpace(configuration.PrefixDirectory)
            || !Path.IsPathFullyQualified(configuration.WineExecutable)
            || !Path.IsPathFullyQualified(configuration.PrefixDirectory)
            || !File.Exists(configuration.WineExecutable) || !Directory.Exists(configuration.PrefixDirectory))
        {
            throw new IOException("Configure an installed GPTK runtime and its Windows Steam prefix.");
        }

        if (!Path.GetFileName(configuration.WineExecutable).Equals("wine64", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Select the wine64 executable supplied with your GPTK runtime.");
        }

        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configuration.WineExecutable)!, ".."));
        if (!Directory.Exists(Path.Combine(root, "lib", "external", "D3DMetal.framework"))
            && !Directory.Exists(Path.Combine(root, "lib", "D3DMetal.framework")))
        {
            throw new IOException("D3DMetal was not found beside this runtime. Choose a complete GPTK installation.");
        }

        if (File.Exists(Path.Combine(configuration.PrefixDirectory, "cxbottle.conf")))
        {
            throw new IOException("Choose a separate Windows Steam prefix created with GPTK.");
        }

        _ = SteamExecutable(configuration);
    }
}
