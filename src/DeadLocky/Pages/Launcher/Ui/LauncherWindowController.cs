using DeadLocky.Features.LaunchGame.Api;
using DeadLocky.Features.LaunchGame.Ui;
using DeadLocky.Features.ManageMods.Api;
using DeadLocky.Features.ManageMods.Ui;
using DeadLocky.Features.PrepareGame.Api;
using DeadLocky.Features.PrepareGame.Ui;
using DeadLocky.Shared.Api.Directories;
using DeadLocky.Shared.Ui;

namespace DeadLocky.Pages.Launcher.Ui;

public sealed class LauncherWindowController : NSWindowController
{
    private readonly NSView _launchContainer = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly GamePreparationView _selection;
#if DEBUG
    private readonly NSButton _testSetup = LauncherStyle.Button("Test setup…");
#endif
    private readonly NSView _launcherPage;
    private readonly NSButton _launcherTab = LauncherStyle.Button("Launcher", true);
    private readonly NSButton _modsTab = LauncherStyle.Button("Mods");
    private ModManagerView? _mods;
    private bool _disposed;
    private LaunchGameView? _launch;
    private bool _wasGameActive;

    public LauncherWindowController(Func<NSWindow, IDirectoryPicker> createPicker,
        IGamePreparationService preparationService) : base(CreateWindow())
    {
        ArgumentNullException.ThrowIfNull(createPicker);
        ArgumentNullException.ThrowIfNull(preparationService);
        _selection = new GamePreparationView(createPicker(Window), preparationService);
        Window.DidBecomeKey += OnWindowActivated;
#if DEBUG
        _testSetup.Activated += OnTestSetup;
#endif
        var content = new LauncherBackdropView();
        var page = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        _launcherPage = page;
        content.AddSubview(page);
        NSImageView mark = LauncherStyle.Symbol("circle.hexagongrid.fill", LauncherStyle.Accent);
        NSTextField title = LauncherStyle.Label("DeadLocky", 60, NSFontWeight.Semibold);
        NSTextField description = LauncherStyle.Label("Your game. Your Mac. Your way.", 16, NSFontWeight.Regular,
            LauncherStyle.SecondaryText);
        foreach (NSView view in new[] { mark, title, description, _selection, _launchContainer })
        {
            page.AddSubview(view);
        }

#if DEBUG
        page.AddSubview(_testSetup);
        NSLayoutConstraint.ActivateConstraints(
        [
            _testSetup.TrailingAnchor.ConstraintEqualTo(page.TrailingAnchor),
            _testSetup.CenterYAnchor.ConstraintEqualTo(title.CenterYAnchor)
        ]);
#endif
        Window.ContentView = content;
        NSLayoutConstraint preferredWidth = page.WidthAnchor.ConstraintEqualTo(724);
        preferredWidth.Priority = 750;
        NSLayoutConstraint.ActivateConstraints(
        [
            page.CenterXAnchor.ConstraintEqualTo(content.CenterXAnchor),
            page.CenterYAnchor.ConstraintEqualTo(content.CenterYAnchor),
            page.WidthAnchor.ConstraintLessThanOrEqualTo(content.WidthAnchor, 1, -96),
            page.WidthAnchor.ConstraintLessThanOrEqualTo(724),
            preferredWidth,
            mark.LeadingAnchor.ConstraintEqualTo(page.LeadingAnchor),
            mark.CenterYAnchor.ConstraintEqualTo(title.CenterYAnchor),
            mark.WidthAnchor.ConstraintEqualTo(48),
            mark.HeightAnchor.ConstraintEqualTo(48),
            title.LeadingAnchor.ConstraintEqualTo(mark.TrailingAnchor, 18),
            title.TopAnchor.ConstraintEqualTo(page.TopAnchor),
            description.LeadingAnchor.ConstraintEqualTo(page.LeadingAnchor),
            description.TopAnchor.ConstraintEqualTo(title.BottomAnchor, 8),
            _selection.LeadingAnchor.ConstraintEqualTo(page.LeadingAnchor),
            _selection.TrailingAnchor.ConstraintEqualTo(page.TrailingAnchor),
            _selection.TopAnchor.ConstraintEqualTo(description.BottomAnchor, 24),
            _launchContainer.TopAnchor.ConstraintEqualTo(_selection.BottomAnchor, 14),
            _launchContainer.LeadingAnchor.ConstraintEqualTo(_selection.LeadingAnchor),
            _launchContainer.TrailingAnchor.ConstraintEqualTo(_selection.TrailingAnchor),
            _launchContainer.BottomAnchor.ConstraintEqualTo(page.BottomAnchor)
        ]);
    }

    public void ConfigureMods(ModWorkspace workspace, ModCatalogClient catalog)
    {
        if (_mods is not null)
        {
            throw new InvalidOperationException("Mods are already configured.");
        }

        NSView content = Window.ContentView!;
        _mods = new ModManagerView(Window, workspace, catalog) { Hidden = true };
        content.AddSubview(_mods);
        content.AddSubview(_launcherTab);
        content.AddSubview(_modsTab);
        _launcherTab.Activated += OnLauncherTab;
        _modsTab.Activated += OnModsTab;
        _mods.BusyChanged += OnModsBusyChanged;
        NSLayoutConstraint.ActivateConstraints(
        [
            _launcherTab.BottomAnchor.ConstraintEqualTo(content.BottomAnchor, -24),
            _launcherTab.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, 28),
            _modsTab.LeadingAnchor.ConstraintEqualTo(_launcherTab.TrailingAnchor, 8),
            _modsTab.CenterYAnchor.ConstraintEqualTo(_launcherTab.CenterYAnchor),
            _mods.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, 28),
            _mods.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor, -28),
            _mods.TopAnchor.ConstraintEqualTo(content.TopAnchor, 56),
            _mods.BottomAnchor.ConstraintEqualTo(_launcherTab.TopAnchor, -20)
        ]);
        _mods.SetGameBusy(_launch?.IsActive == true || _selection.IsInstallationBusy);
        _launch?.SetProfileSummary(_mods.ProfileSummary);
    }

    private void OnLauncherTab(object? sender, EventArgs args)
    {
        ShowMods(false);
    }

    private void OnModsTab(object? sender, EventArgs args)
    {
        ShowMods(true);
    }

    private void ShowMods(bool show)
    {
        _launcherPage.Hidden = show;
        _mods?.Hidden = !show;

        _launcherTab.BezelColor = show ? null : LauncherStyle.Accent;
        _modsTab.BezelColor = show ? LauncherStyle.Accent : null;
    }

    private void OnModsBusyChanged(object? sender, EventArgs args)
    {
        if (_mods is { } mods)
        {
            _launch?.SetProfileSummary(mods.ProfileSummary);
        }

        OnPreparationChanged(sender, args);
    }

    public void ConfigureGameLauncher(IGameLauncher launcher)
    {
        if (_launch is not null)
        {
            throw new InvalidOperationException("Game launcher is already configured.");
        }

        _launch = new LaunchGameView(Window, launcher);
        _launchContainer.AddSubview(_launch);
        NSLayoutConstraint.ActivateConstraints(
        [
            _launch.TopAnchor.ConstraintEqualTo(_launchContainer.TopAnchor),
            _launch.BottomAnchor.ConstraintEqualTo(_launchContainer.BottomAnchor),
            _launch.LeadingAnchor.ConstraintEqualTo(_launchContainer.LeadingAnchor),
            _launch.TrailingAnchor.ConstraintEqualTo(_launchContainer.TrailingAnchor)
        ]);
        _selection.StateChanged += OnPreparationChanged;
        _launch.ActivityChanged += OnLaunchChanged;
        OnPreparationChanged(this, EventArgs.Empty);
    }

    private void OnPreparationChanged(object? sender, EventArgs args)
    {
        _launch?.SetInstallation(_selection.DirectoryPath, _selection.IsDownloaded,
            _selection.IsInstallationBusy || _mods?.IsBusy == true);
        _mods?.SetGameBusy(_launch?.IsActive == true || _selection.IsInstallationBusy);
    }

    public void UseSteamClientPreparation()
    {
        if (_launch is null)
        {
            throw new InvalidOperationException("Configure the game launcher first.");
        }

        _selection.UseSteamClient(_launch.OpenSteamAccountAsync);
        _selection.SetRuntimeSetup(_launch.EnsureRuntimeAsync);
    }

    public async Task ShowRuntimeSetupAsync()
    {
        if (_launch is null)
        {
            return;
        }

        await _launch.ShowRuntimeSetupAsync();
        await _selection.RefreshSteamClientAsync();
    }

#if DEBUG
    public Task TestRuntimeSetupAsync()
    {
        return _launch?.TestRuntimeSetupAsync() ?? Task.CompletedTask;
    }
#endif

#if DEBUG
    private void OnTestSetup(object? sender, EventArgs args)
    {
        _ = UiAction.RunAsync(Window, TestRuntimeSetupAsync);
    }
#endif

    private void OnLaunchChanged(object? sender, EventArgs args)
    {
        bool active = _launch?.IsActive == true;
        bool finished = _wasGameActive && !active;
        _wasGameActive = active;
        _selection.SetGameRunning(active);
        _mods?.SetGameBusy(active || _selection.IsInstallationBusy);
        if (finished)
        {
            _ = _selection.RefreshSteamClientAsync();
        }
    }

    private void OnWindowActivated(object? sender, EventArgs args)
    {
        _ = _selection.RefreshSteamClientAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            Window.DidBecomeKey -= OnWindowActivated;
#if DEBUG
            _testSetup.Activated -= OnTestSetup;
#endif
            _selection.StateChanged -= OnPreparationChanged;
            if (_launch is not null)
            {
                _launch.ActivityChanged -= OnLaunchChanged;
                _launch.Dispose();
            }

            _launcherTab.Activated -= OnLauncherTab;
            _modsTab.Activated -= OnModsTab;
            if (_mods is not null)
            {
                _mods.BusyChanged -= OnModsBusyChanged;
                _mods.Dispose();
            }

            _selection.Dispose();
        }

        base.Dispose(disposing);
    }

    private static NSWindow CreateWindow()
    {
        const NSWindowStyle style = NSWindowStyle.Titled | NSWindowStyle.Closable
                                                         | NSWindowStyle.Miniaturizable | NSWindowStyle.Resizable |
                                                         NSWindowStyle.FullSizeContentView;
        var window = new NSWindow(new CGRect(0, 0, 1100, 850), style, NSBackingStore.Buffered, false)
        {
            Title = "DeadLocky",
            CollectionBehavior = NSWindowCollectionBehavior.FullScreenPrimary,
            TitleVisibility = NSWindowTitleVisibility.Hidden,
            TitlebarAppearsTransparent = true,
            MovableByWindowBackground = true,
            Appearance = NSAppearance.GetAppearance(NSAppearance.NameDarkAqua),
            BackgroundColor = LauncherStyle.Background,
            ContentMinSize = new CGSize(1024, 780)
        };
        window.Center();
        return window;
    }
}
