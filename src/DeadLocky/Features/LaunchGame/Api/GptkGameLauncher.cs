using System.Diagnostics;
using DeadLocky.Entities.Steam.Model;
using DeadLocky.Features.LaunchGame.Model;
using DeadLocky.Shared.Api.Diagnostics;

namespace DeadLocky.Features.LaunchGame.Api;

public sealed class GptkGameLauncher : IGameLauncher, IGameExitPreferences, IRuntimeSetupProvider, IEmergencyGameStopper
{
    private readonly GameExitPreferencesStore _preferences;
    private readonly Func<string, CancellationToken, Task>? _prepareLaunch;
    private readonly Lock _processStart = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _start = new(1, 1);
    private readonly GptkConfigurationStore _store;
    private readonly string _storePath;
    private bool _closeSteamAfterExit;
    private bool _emergencyStopRequested;

    public GptkGameLauncher(string configurationPath,
        Func<string, CancellationToken, Task>? prepareLaunch = null)
    {
        _prepareLaunch = prepareLaunch;
        _storePath = configurationPath;
        _store = new GptkConfigurationStore(configurationPath);
        Configuration = _store.Read() ?? _store.DiscoverOwnedInstallation();
        _preferences = new GameExitPreferencesStore(Path.ChangeExtension(configurationPath, ".preferences.json"));
        _closeSteamAfterExit = _preferences.Read().CloseSteamAfterExit;
        RuntimeSetup = new RuntimeSetupInstaller(Path.GetDirectoryName(Path.GetFullPath(configurationPath))!);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return AppLog.RunAsync("game.emergency-stop", () => StopCoreAsync(cancellationToken));
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        lock (_processStart)
        {
            _emergencyStopRequested = true;
        }

        await _shutdown.CancelAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        await _start.WaitAsync(token);
        try
        {
            GameLaunchConfiguration? configuration = Configuration;
            if (configuration is null)
            {
                return;
            }

            // Stop the prefix's server first so Wine clients cannot respawn each other.
            string server = Path.Combine(Path.GetDirectoryName(configuration.WineExecutable)!, "wineserver");
            if (File.Exists(server))
            {
                var stop = new ProcessStartInfo(server)
                {
                    UseShellExecute = false,
                    Environment = { ["WINEPREFIX"] = configuration.PrefixDirectory }
                };
                stop.ArgumentList.Add("-k");
                using Process process = DiagnosticProcess.Start(stop, "wine.stop-prefix");
                try
                {
                    await process.WaitForExitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }

                    throw;
                }
            }

            await Task.Run(() => MacGameProcessScanner.StopPrefixProcessesAsync(configuration, token), token);
        }
        finally
        {
            _ = _start.Release();
        }
    }

    public bool CloseSteamAfterExit
    {
        get => Volatile.Read(ref _closeSteamAfterExit);
        set
        {
            _preferences.Save(new GameExitPreferences(value));
            Volatile.Write(ref _closeSteamAfterExit, value);
        }
    }

    public GameLaunchConfiguration? Configuration { get; private set; }

    public void Configure(GameLaunchConfiguration configuration)
    {
        GptkConfigurationStore.Validate(configuration);
        _store.Save(configuration);
        Configuration = configuration;
    }

    public Task OpenSteamAsync(CancellationToken cancellationToken)
    {
        return AppLog.RunAsync("steam.launch", () => OpenSteamCoreAsync(cancellationToken));
    }

    private async Task OpenSteamCoreAsync(CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = operation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        GameLaunchConfiguration configuration = ValidConfiguration();
        await PrepareCrashHandlingAsync(configuration, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Task<int> command = StartSteam(configuration, false, cancellationToken);
        _ = await Task.WhenAny(command, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (command.IsCompletedSuccessfully && command.Result != 0)
        {
            throw new IOException($"GPTK failed to start Windows Steam (exit code {command.Result}).");
        }
    }

    public async Task<IGameSession?> FindRunningAsync(string directory, CancellationToken cancellationToken)
    {
        if (Configuration is not { } configuration)
        {
            return null;
        }

        int? pid = await Task.Run(() => MacGameProcessScanner.FindProcessId(configuration, directory), cancellationToken);
        if (pid is null) { return null; }
        AppLog.Event("game.process-observation", "attached", fields: AppLog.Data(("process.id", pid), ("game.directory", directory)));
        return CreateSession(configuration, directory);
    }

    public Task<IGameSession> LaunchAsync(string directory, CancellationToken cancellationToken)
    {
        return AppLog.RunAsync("game.launch", () => LaunchCoreAsync(directory, cancellationToken), AppLog.Data(("game.directory", directory)));
    }

    private async Task<IGameSession> LaunchCoreAsync(string directory, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = operation.Token;
        await _start.WaitAsync(cancellationToken);
        try
        {
            GameLaunchConfiguration configuration = ValidConfiguration();
            await PrepareCrashHandlingAsync(configuration, cancellationToken);
            if (!File.Exists(Path.Combine(directory, "game", "bin", "win64", "deadlock.exe")))
            {
                throw new IOException("Deadlock's executable is missing. Verify the download first.");
            }

            if (await FindRunningAsync(directory, cancellationToken) is { } existing)
            {
                AppLog.Event("game.launch.result", "attached to existing game");
                return existing;
            }

            SteamInstallationLink.Register(GptkConfigurationStore.SteamExecutable(configuration), directory);
            cancellationToken.ThrowIfCancellationRequested();
            if (_prepareLaunch is not null)
            {
                await _prepareLaunch(directory, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }

            Task<int> command = StartSteam(configuration, true, cancellationToken);
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromMinutes(2))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await Task.Run(() => MacGameProcessScanner.FindProcessId(configuration, directory), cancellationToken) is { } pid)
                {
                    AppLog.Event("game.launch.result", "game process detected", fields: AppLog.Data(("game.startupMs", deadline.Elapsed.TotalMilliseconds), ("process.id", pid)));
                    return CreateSession(configuration, directory);
                }

                if (command.IsCompletedSuccessfully && command.Result != 0)
                {
                    throw new IOException($"GPTK failed to start Windows Steam (exit code {command.Result}).");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }

            AppLog.Event("game.launch.result", "process detection timed out", fields: AppLog.Data(("steam.commandCompleted", command.IsCompleted), ("game.startupMs", deadline.Elapsed.TotalMilliseconds)));
            throw new IOException("Windows Steam did not start Deadlock. Finish signing in there, "
                                  + "let Steam recognize the linked game files, then retry Play.");
        }
        finally
        {
            _ = _start.Release();
        }
    }

    public void Dispose()
    {
        _shutdown.Dispose();
    }

    public IRuntimeSetup RuntimeSetup { get; }

    public IRuntimeSetup CreateTestSetup()
    {
        string root = Path.GetDirectoryName(Path.GetFullPath(_storePath))!;
        return new RuntimeSetupInstaller(Path.Combine(root, "SetupTests", Guid.NewGuid().ToString("N")));
    }

    public SteamAccount? ReadSavedSteamAccount()
    {
        return Configuration is null
            ? null
            : SteamClientState.ReadSavedAccount(
                Path.GetDirectoryName(GptkConfigurationStore.SteamExecutable(Configuration))!);
    }

    public bool IsSteamInstallationReady(string directory)
    {
        return Configuration is not null
               && SteamClientState.IsInstalled(
                   Path.GetDirectoryName(GptkConfigurationStore.SteamExecutable(Configuration))!,
                   directory, true);
    }

    public Task DownloadWithSteamAsync(string directory, CancellationToken token)
    {
        return AppLog.RunAsync("game.download", () => DownloadWithSteamCoreAsync(directory, token), AppLog.Data(("game.directory", directory)));
    }

    private async Task DownloadWithSteamCoreAsync(string directory, CancellationToken token)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        token = operation.Token;
        GameLaunchConfiguration configuration = ValidConfiguration();
        await PrepareCrashHandlingAsync(configuration, token);
        string executable = GptkConfigurationStore.SteamExecutable(configuration);
        string steamDirectory = Path.GetDirectoryName(executable)!;
        _ = Directory.CreateDirectory(directory);
        SteamInstallationLink.Register(executable, directory);
        string manifest = SteamClientState.ManifestPath(steamDirectory);
        DateTime before = File.GetLastWriteTimeUtc(manifest);
        ProcessStartInfo start = CreateStartInfo(configuration, false);
        start.ArgumentList.Add("steam://install/1422450");
        token.ThrowIfCancellationRequested();
        Process process = StartProcess(start, token);
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Task<int> command = WaitForCommandAsync(process);
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            if (command.IsCompletedSuccessfully && command.Result != 0)
            {
                throw new IOException("Steam installation failed to open.");
            }

            if (File.GetLastWriteTimeUtc(manifest) != before && SteamClientState.IsInstalled(steamDirectory, directory))
            {
                return;
            }
        }
    }

    public void EnsureGameFilesIdle(string directory)
    {
        GameLaunchConfiguration configuration = ValidConfiguration();
        if (MacGameProcessScanner.IsRunning(configuration, directory))
        {
            AppLog.Event("game.files-idle", "blocked: game is running", fields: AppLog.Data(("game.directory", directory)));
            throw new IOException("Close Deadlock before applying a mod profile.");
        }

        string steam = Path.GetDirectoryName(GptkConfigurationStore.SteamExecutable(configuration))!;
        if (SteamClientState.IsInstalled(steam, directory)) { return; }
        AppLog.Event("game.files-idle", "blocked: Steam installation is not ready", fields: AppLog.Data(("game.directory", directory)));
        throw new IOException("Wait for Steam to finish installing or updating Deadlock before applying mods.");
    }

    private GameLaunchConfiguration ValidConfiguration()
    {
        GameLaunchConfiguration configuration = Configuration
                                                ?? throw new IOException(
                                                    "Set up GPTK and a Windows Steam prefix before playing.");
        GptkConfigurationStore.Validate(configuration);
        AppLog.Event("runtime.launch-configuration", "validated", fields: AppLog.Data(("wine.executable", configuration.WineExecutable),
            ("wine.prefix", configuration.PrefixDirectory), ("steam.executable", GptkConfigurationStore.SteamExecutable(configuration))));
        return configuration;
    }

    private GameProcessSession CreateSession(GameLaunchConfiguration configuration, string directory)
    {
        return new GameProcessSession(() => MacGameProcessScanner.IsRunning(configuration, directory),
            token => CloseSteamAfterExit
                ? CloseSteamAfterGameAsync(configuration, directory, token)
                : Task.CompletedTask);
    }

    private async Task PrepareCrashHandlingAsync(GameLaunchConfiguration configuration, CancellationToken token)
    {
        ProcessStartInfo start = CreateStartInfo(configuration, false);
        start.ArgumentList.Clear();
        foreach (string argument in new[]
                 {
                     "reg", "add", @"HKCU\Software\Wine\WineDbg", "/v", "ShowCrashDialog", "/t", "REG_DWORD", "/d", "0",
                     "/f"
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = StartProcess(start, token);
        Task output = process.StandardOutput.ReadToEndAsync(token);
        Task error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        await Task.WhenAll(output, error);
        if (process.ExitCode != 0)
        {
            throw new IOException("Unable to configure automatic crash-dialog dismissal.");
        }
    }

    private async Task CloseSteamAfterGameAsync(GameLaunchConfiguration configuration, string directory,
        CancellationToken token)
    {
        GptkConfigurationStore.Validate(configuration);
        if (MacGameProcessScanner.IsRunning(configuration, directory))
        {
            return;
        }

        ProcessStartInfo shutdown = CreateStartInfo(configuration, false);
        shutdown.ArgumentList.Add("-shutdown");
        using (Process process = StartProcess(shutdown, token))
        {
            Task output = process.StandardOutput.ReadToEndAsync(token);
            Task error = process.StandardError.ReadToEndAsync(token);
            _ = await Task.WhenAny(process.WaitForExitAsync(token), Task.Delay(TimeSpan.FromSeconds(8), token));
            token.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            _ = Task.WhenAll(output, error);
        }

        if (MacGameProcessScanner.IsRunning(configuration, directory))
        {
            return;
        }

        string server = Path.Combine(Path.GetDirectoryName(configuration.WineExecutable)!, "wineserver");
        if (!File.Exists(server))
        {
            throw new IOException("The GPTK wineserver executable is missing.");
        }

        var stop = new ProcessStartInfo(server)
        {
            UseShellExecute = false,
            Environment = { ["WINEPREFIX"] = configuration.PrefixDirectory }
        };
        stop.ArgumentList.Add("-k");
        token.ThrowIfCancellationRequested();
        using Process cleanup = DiagnosticProcess.Start(stop, "wine.stop-prefix");
        await cleanup.WaitForExitAsync(token);
        if (cleanup.ExitCode != 0)
        {
            throw new IOException("GPTK could not stop leftover Steam processes.");
        }

        if (!MacGameProcessScanner.IsRunning(configuration, directory))
        {
            await MacGameProcessScanner.StopPrefixProcessesAsync(configuration, token);
        }
    }

    private Task<int> StartSteam(GameLaunchConfiguration configuration, bool launchGame, CancellationToken token)
    {
        ProcessStartInfo start = CreateStartInfo(configuration, launchGame);
        Process process = StartProcess(start, token);
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return WaitForCommandAsync(process);
    }

    private Process StartProcess(ProcessStartInfo start, CancellationToken token)
    {
        lock (_processStart)
        {
            token.ThrowIfCancellationRequested();
            return _emergencyStopRequested
                ? throw new OperationCanceledException("Emergency shutdown has been requested.")
                : DiagnosticProcess.Start(start, "wine.command");
        }
    }

    private static async Task<int> WaitForCommandAsync(Process process)
    {
        using (process)
        {
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(GameLaunchConfiguration configuration, bool launchGame)
    {
        var start = new ProcessStartInfo(configuration.WineExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = configuration.PrefixDirectory
        };
        start.ArgumentList.Add(GptkConfigurationStore.SteamExecutable(configuration));
        if (launchGame)
        {
            start.ArgumentList.Add("-applaunch");
            start.ArgumentList.Add("1422450");
            start.ArgumentList.Add("-dx11");
        }

        start.Environment["WINEPREFIX"] = configuration.PrefixDirectory;
        start.Environment["WINEESYNC"] = "1";
        start.Environment["WINEDEBUG"] = "-all";
        start.Environment["MTL_HUD_ENABLED"] = "0";
        start.Environment["WINEDLLOVERRIDES"] = "d3d11,d3d12,dxgi=n,b";
        string runtime = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configuration.WineExecutable)!, ".."));
        start.Environment["DYLD_FALLBACK_LIBRARY_PATH"] = Path.Combine(runtime, "lib") + ":/usr/local/lib:/usr/lib";
        return start;
    }
}
