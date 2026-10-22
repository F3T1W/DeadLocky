using DeadLocky.Features.LaunchGame.Model;
using DeadLocky.Shared.Api.Diagnostics;

namespace DeadLocky.Features.LaunchGame.Api;

internal sealed class RuntimeSetupInstaller(string root, RuntimeSetupPlatform? platform = null) : IRuntimeSetup
{
    private static readonly RuntimeDownload Wine = new("Wine 11",
        "https://github.com/gauthierpiarrette/highball-engine/releases/download/"
        + "engine-wine-11.0-20261006T161716Z-0021-es2-compat/"
        + "engine-wine-11.0-20261006T161716Z-0021-es2-compat.tar.gz",
        "51355c786168c5c84eec51e3bc364c8a1acd87437e1418b27887a51d42a5657b");

    private static readonly RuntimeDownload Libraries = new("Wine libraries",
        "https://github.com/Gcenx/game-porting-toolkit/releases/download/"
        + "Game-Porting-Toolkit-3.0-3/game-porting-toolkit-3.0-3.tar.xz",
        "d377683937340f914823dbb2e1252b329cbf834ff58907d0293db8cebf0e392e");

    private static readonly RuntimeDownload Steam = new("Windows Steam",
        "https://cdn.akamai.steamstatic.com/client/installer/SteamSetup.exe",
        "7d3654531c32d941b8cae81c4137fc542172bfa9635f169cb392f245a0a12bcb");

    private readonly RuntimeSetupPlatform _platform = platform ?? new RuntimeSetupPlatform();

    public GameLaunchConfiguration? FindExisting()
    {
        return new GptkConfigurationStore(Path.Combine(root, "launch.json")).Read()
               ?? new GptkConfigurationStore(Path.Combine(root, "launch.json")).DiscoverOwnedInstallation();
    }

    public string? FindAppleDownload()
    {
        return AppleGraphicsImport.FindMounted();
    }

    public Task InstallRosettaAsync(CancellationToken token)
    {
        return _platform.RunAsync("/usr/sbin/softwareupdate", ["--install-rosetta", "--agree-to-license"], null, null,
            token);
    }

    public Task<GameLaunchConfiguration> InstallAsync(string appleDownload,
        IProgress<RuntimeSetupProgress> progress, CancellationToken token)
    {
        return AppLog.RunAsync("runtime.install", () => InstallCoreAsync(appleDownload, progress, token));
    }

    private async Task<GameLaunchConfiguration> InstallCoreAsync(string appleDownload,
        IProgress<RuntimeSetupProgress> progress, CancellationToken token)
    {
        _ = Directory.CreateDirectory(root);
        await using var exclusion = new FileStream(Path.Combine(root, ".runtime-setup.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        if (FindExisting() is { } existing)
        {
            progress.Report(new RuntimeSetupProgress("Ready", "Your existing environment is ready.", 1));
            return existing;
        }

        progress.Report(new RuntimeSetupProgress("Check", "Checking your Mac…"));
        await _platform.CheckHostAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(40));
        token = timeout.Token;
        await using var apple = new AppleGraphicsImport(_platform);
        progress.Report(new RuntimeSetupProgress("Apple graphics", "Finding graphics in your Apple download…"));
        string redist = await apple.ResolveAsync(appleDownload, token);
        string runtime = Path.Combine(root, "Runtime", "ManagedWine11-r21");
        string engine = Path.Combine(runtime, "engine");
        string prefix = Path.Combine(root, "Prefixes", "Steam-Managed");
        var configuration = new GameLaunchConfiguration(Path.Combine(engine, "bin", "wine64"), prefix);
        string work = Path.Combine(root, ".setup-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        bool ownsRuntime = false;
        bool ownsPrefix = false;
        try
        {
            await RemoveIncompleteAsync(configuration, runtime, token);
            string cache = Path.Combine(root, "Cache", "RuntimeSetup");
            string wine = Path.Combine(cache, "wine11.tar.gz");
            string libraries = Path.Combine(cache, "libraries.tar.xz");
            string steam = Path.Combine(cache, "SteamSetup.exe");
            foreach ((RuntimeDownload download, string path) in new[]
                         { (Wine, wine), (Libraries, libraries), (Steam, steam) })
            {
                progress.Report(new RuntimeSetupProgress("Download", $"Downloading {download.Name}…"));
                await _platform.DownloadAsync(download, path, progress, token);
            }

            progress.Report(new RuntimeSetupProgress("Environment", "Preparing your game environment…"));
            _ = await RunAsync("/usr/bin/tar", ["-xf", wine, "-C", work], token);
            string dependencies = Path.Combine(work, "dependencies");
            _ = Directory.CreateDirectory(dependencies);
            _ = await RunAsync("/usr/bin/tar", ["-xf", libraries, "-C", dependencies], token);
            string stagedEngine = Path.Combine(work, "engine");
            string lib = Path.Combine(stagedEngine, "lib");
            string dependencyLib = Path.Combine(dependencies, "Game Porting Toolkit.app", "Contents", "Resources",
                "wine", "lib");
            foreach (string dependency in Directory.EnumerateFiles(dependencyLib, "*.dylib"))
            {
                _ = await RunAsync("/usr/bin/ditto", [dependency, Path.Combine(lib, Path.GetFileName(dependency))],
                    token);
            }

            _ = await RunAsync("/usr/bin/ditto", [Path.Combine(redist, "lib"), lib], token);
            string wine64 = Path.Combine(stagedEngine, "bin", "wine64");
            if (!File.Exists(wine64))
            {
                _ = File.CreateSymbolicLink(wine64, "wine");
            }

            _ = Directory.CreateDirectory(Path.GetDirectoryName(runtime)!);
            _ = Directory.CreateDirectory(runtime);
            ownsRuntime = true;
            await File.WriteAllTextAsync(Path.Combine(runtime, ".deadlocky-owned"), "Wine11-r21", token);
            Directory.Move(stagedEngine, engine);
            _ = Directory.CreateDirectory(prefix);
            ownsPrefix = true;
            await File.WriteAllTextAsync(Path.Combine(prefix, ".deadlocky-owned"), "Steam-Managed", token);
            _ = await WineAsync(configuration, ["--version"], token);
            progress.Report(new RuntimeSetupProgress("Steam", "Creating a private Steam environment…"));
            _ = await WineAsync(configuration, ["wineboot", "-u"], token);
            _ = await WineAsync(configuration,
            [
                "reg", "add", @"HKCU\Software\Wine\AppDefaults\steamwebhelper.exe", "/v", "CommandLineAppend",
                "/t", "REG_SZ", "/d", "--disable-gpu --single-process", "/f"
            ], token);
            _ = await WineAsync(configuration,
            [
                "reg", "add", @"HKCU\Software\Wine\WineDbg", "/v", "ShowCrashDialog", "/t", "REG_DWORD",
                "/d", "0", "/f"
            ], token);
            progress.Report(new RuntimeSetupProgress("Steam", "Installing Windows Steam…"));
            _ = await WineAsync(configuration, [steam, "/S"], token);
            GptkConfigurationStore.Validate(configuration);
            await StopAsync(configuration);
            token.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(Path.Combine(prefix, ".deadlocky-ready"), "1", token);
            progress.Report(new RuntimeSetupProgress("Ready", "Environment ready. Sign in to Steam to continue.", 1));
            return configuration;
        }
        catch
        {
            if (ownsPrefix)
            {
                await StopAsync(configuration);
                Directory.Delete(prefix, true);
            }

            if (ownsRuntime)
            {
                Directory.Delete(runtime, true);
            }

            throw;
        }
        finally
        {
            Directory.Delete(work, true);
        }
    }

    private async Task RemoveIncompleteAsync(GameLaunchConfiguration configuration, string runtime,
        CancellationToken token)
    {
        string prefix = configuration.PrefixDirectory;
        foreach ((string path, string identity) in new[] { (prefix, "Steam-Managed"), (runtime, "Wine11-r21") })
        {
            string marker = Path.Combine(path, ".deadlocky-owned");
            if (Directory.Exists(path) && (new DirectoryInfo(path).LinkTarget is not null
                                           || !File.Exists(marker) ||
                                           await File.ReadAllTextAsync(marker, token) != identity))
            {
                throw new IOException("The setup destination contains another installation. "
                                      + "Your existing files have been preserved.");
            }
        }

        token.ThrowIfCancellationRequested();
        if (Directory.Exists(prefix))
        {
            await StopAsync(configuration);
            Directory.Delete(prefix, true);
        }

        if (Directory.Exists(runtime))
        {
            Directory.Delete(runtime, true);
        }
    }

    private Task<string> RunAsync(string executable, string[] arguments, CancellationToken token)
    {
        return _platform.RunAsync(executable, arguments, null, null, token);
    }

    private Task<string> WineAsync(GameLaunchConfiguration configuration, string[] arguments, CancellationToken token)
    {
        return _platform.RunAsync(configuration.WineExecutable, arguments, configuration.PrefixDirectory,
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configuration.WineExecutable)!, "..", "lib")), token);
    }

    private async Task StopAsync(GameLaunchConfiguration configuration)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string server = Path.Combine(Path.GetDirectoryName(configuration.WineExecutable)!, "wineserver");
        if (File.Exists(server))
        {
            try
            {
                _ = await _platform.RunAsync(server, ["-k"], configuration.PrefixDirectory, null, timeout.Token);
            }
            catch (IOException)
            {
            }
        }

        await _platform.StopPrefixProcessesAsync(configuration, timeout.Token);
    }
}
