using DeadLocky.Features.LaunchGame.Api;
using DeadLocky.Features.LaunchGame.Model;
using DeadLocky.Shared.Ui;

namespace DeadLocky.Features.LaunchGame.Ui;

public sealed class LaunchGameView : NSStackView
{
    private readonly NSButton _closeSteam = new()
    {
        Title = "Close Steam after Deadlock exits",
        TranslatesAutoresizingMaskIntoConstraints = false
    };

    private readonly GameLaunchController _controller;

    private readonly NSTextField _detail = LauncherStyle.Label("Apple Game Porting Toolkit", 11,
        NSFontWeight.Regular, LauncherStyle.SecondaryText);

    private readonly NSTextField _error = LauncherStyle.Label("", 12, NSFontWeight.Regular, NSColor.SystemRed);
    private readonly NSButton _emergencyQuit = LauncherStyle.Button("Emergency quit");
    private readonly NSWindow _owner;
    private readonly NSButton _play = LauncherStyle.Button("Play", true);
    private readonly IRuntimeSetup? _runtimeSetup;
    private readonly NSButton _setup = LauncherStyle.Button("Set up GPTK…");
#if DEBUG
    private readonly IRuntimeSetupProvider? _setupProvider;
#endif
    private readonly NSLayoutConstraint _setupWidth;
    private readonly NSTextField _title = LauncherStyle.Label("Ready when you are.", 14, NSFontWeight.Medium);
    private string _profileSummary = "Ready to play on your Mac";
    private bool _disposed;
#if DEBUG
    private bool _isTesting;
#endif

    public LaunchGameView(NSWindow owner, IGameLauncher launcher)
    {
        _owner = owner;
        _controller = new GameLaunchController(launcher);
        var setupProvider = launcher as IRuntimeSetupProvider;
        _runtimeSetup = setupProvider?.RuntimeSetup;
#if DEBUG
        _setupProvider = setupProvider;
#endif
        _closeSteam.SetButtonType(NSButtonType.Switch);
        _setupWidth = _setup.WidthAnchor.ConstraintEqualTo(0);
        Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        Alignment = NSLayoutAttribute.Leading;
        Spacing = 10;
        TranslatesAutoresizingMaskIntoConstraints = false;
        var content = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        NSView surface = LauncherStyle.Surface(content);
        foreach (NSView view in new NSView[] { _title, _detail, _setup, _closeSteam, _play })
        {
            content.AddSubview(view);
        }

        _detail.LineBreakMode = NSLineBreakMode.TruncatingTail;
        _error.MaximumNumberOfLines = 2;
        _error.UsesSingleLineMode = false;
        _error.LineBreakMode = NSLineBreakMode.ByWordWrapping;
        AddArrangedSubview(surface);
        AddArrangedSubview(_error);
        _emergencyQuit.ContentTintColor = NSColor.SystemRed;
        _emergencyQuit.ToolTip = "Force-stop Deadlock, Windows Steam and Wine in this launcher's prefix, then quit.";
        AddArrangedSubview(_emergencyQuit);
        _emergencyQuit.Activated += OnEmergencyQuit;
        NSLayoutConstraint.ActivateConstraints(
        [
            surface.WidthAnchor.ConstraintEqualTo(WidthAnchor),
            surface.HeightAnchor.ConstraintEqualTo(120),
            _title.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, 24),
            _title.TopAnchor.ConstraintEqualTo(content.TopAnchor, 20),
            _detail.LeadingAnchor.ConstraintEqualTo(_title.LeadingAnchor),
            _detail.CenterYAnchor.ConstraintEqualTo(content.CenterYAnchor),
            _detail.TrailingAnchor.ConstraintLessThanOrEqualTo(_setup.LeadingAnchor, -12),
            _title.TrailingAnchor.ConstraintLessThanOrEqualTo(_setup.LeadingAnchor, -12),
            _play.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor, -24),
            _play.CenterYAnchor.ConstraintEqualTo(content.CenterYAnchor),
            _play.WidthAnchor.ConstraintEqualTo(128),
            _setup.TrailingAnchor.ConstraintEqualTo(_play.LeadingAnchor, -10),
            _setup.CenterYAnchor.ConstraintEqualTo(_play.CenterYAnchor),
            _closeSteam.LeadingAnchor.ConstraintEqualTo(_title.LeadingAnchor),
            _closeSteam.BottomAnchor.ConstraintEqualTo(content.BottomAnchor, -18),
            _closeSteam.TrailingAnchor.ConstraintLessThanOrEqualTo(_play.LeadingAnchor, -12),
            _error.WidthAnchor.ConstraintEqualTo(WidthAnchor)
        ]);
        _play.Activated += OnPlay;
        _setup.Activated += OnSetup;
        _closeSteam.Activated += OnCloseSteamChanged;
        _controller.StateChanged += OnStateChanged;
        Render();
    }

    public bool IsActive => _controller.State.IsStarting || _controller.State.IsRunning
                                                         || _controller.State.EmergencyStopRequested;

    public event EventHandler? ActivityChanged;

    public async Task OpenSteamAccountAsync()
    {
        if (await EnsureRuntimeAsync())
        {
            await _controller.OpenSteamAsync();
        }
    }

    public async Task<bool> EnsureRuntimeAsync()
    {
        if (_controller.Configuration is null)
        {
            await _controller.ConfigureAsync(ChooseAsync);
        }

        return _controller.Configuration is not null;
    }

    public void SetProfileSummary(string summary)
    {
        _profileSummary = summary;
        Render();
    }

    public void SetInstallation(string directory, bool ready, bool busy)
    {
        _controller.SetInstallation(directory, ready, busy);
        if (!busy)
        {
            _ = _controller.CheckRunningAsync();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _controller.StateChanged -= OnStateChanged;
            _emergencyQuit.Activated -= OnEmergencyQuit;
            _play.Activated -= OnPlay;
            _setup.Activated -= OnSetup;
            _closeSteam.Activated -= OnCloseSteamChanged;
            _controller.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnEmergencyQuit(object? sender, EventArgs args)
    {
        _ = UiAction.RunAsync(_owner, async () =>
        {
            if (await _controller.EmergencyStopAsync())
            {
                NSApplication.SharedApplication.Terminate(_emergencyQuit);
            }
        });
    }

    private void OnPlay(object? sender, EventArgs args)
    {
        _ = UiAction.RunAsync(_owner, OnPlayAsync);
    }

    private async Task OnPlayAsync()
    {
        if (_controller.Configuration is null)
        {
            await _controller.ConfigureAsync(ChooseAsync);
        }

        if (_controller.Configuration is not null)
        {
            await _controller.LaunchAsync();
        }
    }

    private void OnCloseSteamChanged(object? sender, EventArgs args)
    {
        _controller.SetCloseSteamAfterExit(_closeSteam.State == NSCellStateValue.On);
    }

    private void OnSetup(object? sender, EventArgs args)
    {
        _ = UiAction.RunAsync(_owner, OnSetupAsync);
    }

    private async Task OnSetupAsync()
    {
        await ShowRuntimeSetupAsync();
    }

    public async Task ShowRuntimeSetupAsync()
    {
        bool accepted = false;
        await _controller.ConfigureAsync(async token =>
        {
            GameLaunchConfiguration? configuration = await ChooseAsync(token);
            accepted = configuration is not null;
            return configuration;
        });
        if (accepted)
        {
            await _controller.OpenSteamAsync();
        }
    }

#if DEBUG
    public async Task TestRuntimeSetupAsync()
    {
        if (IsActive || _isTesting || _setupProvider is null)
        {
            return;
        }

        _isTesting = true;
        try
        {
            await RuntimeSetupWizard.ShowTestAsync(_owner, _setupProvider.CreateTestSetup(), CancellationToken.None);
        }
        finally
        {
            _isTesting = false;
        }
    }
#endif

    private Task<GameLaunchConfiguration?> ChooseAsync(CancellationToken token)
    {
        return RuntimeSetupWizard.ShowAsync(_owner,
            _runtimeSetup ?? throw new InvalidOperationException("Runtime setup is unavailable."), token);
    }

    private void OnStateChanged(object? sender, EventArgs args)
    {
        BeginInvokeOnMainThread(() =>
        {
            if (_disposed)
            {
                return;
            }

            Render();
            ActivityChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void Render()
    {
        GameLaunchState state = _controller.State;
        _play.Title = state.IsRunning ? "Game running" : state.IsStarting ? "Starting…" : "Play";
        _play.Enabled = state.CanLaunch;
        _emergencyQuit.Title = state.IsStopping ? "Stopping…" : "Emergency quit";
        _emergencyQuit.Enabled = _controller.CanEmergencyStop && state is { IsStopping: false, IsConfiguring: false };
        _closeSteam.Enabled = !state.EmergencyStopRequested;
        _title.StringValue = state.IsRunning ? "Enjoy the game."
            : state.IsStarting ? "Opening Deadlock…"
            : state.InstallationReady ? "Ready when you are." : "One step closer.";
        _detail.StringValue = _controller.Configuration is null
            ? "Set up GPTK to play on Mac"
            : _profileSummary;
        _setup.Hidden = _controller.Configuration is not null;
        _setupWidth.Active = _setup.Hidden;
        _setup.Enabled = !IsActive && !state.IsConfiguring;
        _closeSteam.State = _controller.CloseSteamAfterExit ? NSCellStateValue.On : NSCellStateValue.Off;
        _error.StringValue = state.Error ?? "";
        _error.ToolTip = state.Error;
        _error.Hidden = state.Error is null;
    }
}
