using DeadLocky.Entities.Game.Model;
using DeadLocky.Entities.Steam.Model;
using DeadLocky.Features.PrepareGame.Model;
using DeadLocky.Shared.Api.Diagnostics;

namespace DeadLocky.Features.PrepareGame.Api;

public sealed class SteamClientPreparationService(
    string settingsPath,
    Func<SteamAccount?> readAccount,
    Func<CancellationToken, Task> openSteam,
    Func<string, CancellationToken, Task> download,
    Func<string, bool> isInstalled) : IGamePreparationService
{
    private readonly InstallationStore _store = new(settingsPath);

    public event EventHandler? SessionLost
    {
        add { }
        remove { }
    }

    public GameDirectory ReadDirectory()
    {
        return _store.ReadDirectory();
    }

    public void SaveDirectory(GameDirectory directory)
    {
        _store.SaveDirectory(directory);
    }

    public Task<bool> IsDownloadedAsync(GameDirectory directory, CancellationToken cancellationToken)
    {
        return AppLog.RunAsync("game.installation.verify", () => IsDownloadedCoreAsync(directory, cancellationToken), AppLog.Data(("game.directory", directory.Path)));
    }

    private async Task<bool> IsDownloadedCoreAsync(GameDirectory directory, CancellationToken cancellationToken)
    {
        bool ready = isInstalled(directory.Path) || await InstallationStore.IsDownloadedAsync(directory, cancellationToken);
        AppLog.Event("game.installation.result", ready ? "verified" : "not installed or verification failed");
        return ready;
    }

    public Task<SteamAccount?> RestoreSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SteamAccount? account = readAccount();
        AppLog.Event("steam.session", account is null ? "sign-in required" : "saved session available");
        return Task.FromResult(account);
    }

    public async Task<SteamAccount> SignInAsync(CancellationToken cancellationToken)
    {
        await openSteam(cancellationToken);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (readAccount() is { } account)
            {
                AppLog.Event("steam.session", "sign-in detected");
                return account;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    public async Task DownloadAsync(GameDirectory directory, IProgress<GameDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new GameDownloadProgress(0, 0, "Continue installation or verification in Steam."));
        await download(directory.Path, cancellationToken);
    }

    public void Dispose()
    {
    }
}
