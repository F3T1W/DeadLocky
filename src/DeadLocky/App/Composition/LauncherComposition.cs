using System.Runtime.InteropServices;
using DeadLocky.Features.LaunchGame.Api;
using DeadLocky.Features.ManageMods.Api;
using DeadLocky.Features.PrepareGame.Api;
using DeadLocky.Features.ReportProblem.Api;
using DeadLocky.Features.ReportProblem.Model;
using DeadLocky.Features.ReportProblem.Ui;
using DeadLocky.Features.UpdateApp.Api;
using DeadLocky.Features.UpdateApp.Model;
using DeadLocky.Pages.Launcher.Ui;
using DeadLocky.Shared.Api.Diagnostics;
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

    public static ReportWindowController CreateReporter()
    {
        DiagnosticLog log = AppLog.Current ?? throw new InvalidOperationException("Logging is not initialized.");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var metadata = new ReportMetadata(
            NSBundle.MainBundle.InfoDictionary?["CFBundleShortVersionString"]?.ToString() ?? "unknown",
            NSBundle.MainBundle.InfoDictionary?["CFBundleVersion"]?.ToString() ?? "unknown",
            RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), log.SessionId);
        ReportClient client;
        try { client = new ReportClient(NSBundle.MainBundle.InfoDictionary?["DeadLockyReportEndpoint"]?.ToString()); }
        catch (ArgumentException exception)
        {
            AppLog.Event("report.configuration", "invalid endpoint; sending disabled", exception);
            client = new ReportClient(null);
        }

        AppLog.Event("report.configuration", client.IsConfigured ? "sending enabled" : "sending unavailable");
        return new ReportWindowController(new ReportArchive(log,
            Path.Combine(home, "Library", "Caches", "DeadLocky", "Reports")), client, metadata);
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
