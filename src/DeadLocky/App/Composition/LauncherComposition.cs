using DeadLocky.Features.LaunchGame.Api;
using DeadLocky.Features.ManageMods.Api;
using DeadLocky.Features.PrepareGame.Api;
using DeadLocky.Features.UpdateApp.Api;
using DeadLocky.Features.UpdateApp.Model;
using DeadLocky.Pages.Launcher.Ui;
using DeadLocky.Shared.Api.Directories;

namespace DeadLocky.App.Composition;

internal static class LauncherComposition
{
    public static LauncherWindowController CreateLauncher()
    {
        AppVersion version = AppVersion.Parse(NSBundle.MainBundle.InfoDictionary?["CFBundleShortVersionString"]?.ToString())
                             ?? throw new InvalidOperationException("The app bundle has no valid version.");
        string settings = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "DeadLocky", "settings.json");
        string modsRoot = Path.Combine(Path.GetDirectoryName(settings)!, "Mods");
        var services = new LauncherServices(Path.Combine(Path.GetDirectoryName(settings)!, "launch.json"), modsRoot);
        GptkGameLauncher launcher = services.Launcher;
        ModWorkspace workspace = services.Workspace;
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"DeadLocky/{version}");
        var catalog = new ModCatalogClient(http, Path.Combine(modsRoot, "catalog.json"));
        var service = new SteamClientPreparationService(settings, launcher.ReadSavedSteamAccount,
            launcher.OpenSteamAsync, launcher.DownloadWithSteamAsync, launcher.IsSteamInstallationReady);
        var window = new LauncherWindowController(static owner => new AppKitDirectoryPicker(owner), service);
        window.ConfigureGameLauncher(launcher);
        window.UseSteamClientPreparation();
        window.ConfigureMods(workspace, catalog);
        var updatesHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(15), MaxResponseContentBufferSize = 4194304 };
        window.ConfigureUpdates(updatesHttp,
            new MacUpdateInstaller(NSBundle.MainBundle.BundlePath,
                Path.Combine(Path.GetDirectoryName(settings)!, "Updates")), version);
        return window;
    }

    private sealed class LauncherServices
    {
        public LauncherServices(string configuration, string modsRoot)
        {
            Launcher = new GptkGameLauncher(configuration,
                (directory, token) => Workspace!.ApplyAsync(directory, token));
            Workspace = new ModWorkspace(modsRoot, Launcher.EnsureGameFilesIdle);
        }

        public GptkGameLauncher Launcher { get; }
        public ModWorkspace Workspace { get; }
    }
}
