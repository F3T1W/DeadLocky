using DeadLocky.Features.LaunchGame.Api;
using DeadLocky.Features.LaunchGame.Model;
using DeadLocky.Shared.Ui;

namespace DeadLocky.Features.LaunchGame.Ui;

internal sealed class RuntimeSetupWizard : IDisposable
{
    private readonly NSButton _cancel = LauncherStyle.Button("Cancel");
    private readonly NSButton _choose = LauncherStyle.Button("Choose Apple download…");
    private readonly TaskCompletionSource<GameLaunchConfiguration?> _completion = new();
    private readonly NSButton _download = LauncherStyle.Button("Get GPTK from Apple ↗");
    private readonly NSButton _install = LauncherStyle.Button("Set up environment", true);
    private readonly IRuntimeSetup _installer;
    private readonly CancellationTokenSource _lifetime;
    private readonly NSWindow _owner;

    private readonly NSProgressIndicator _progress = new()
    {
        Style = NSProgressIndicatorStyle.Bar,
        MinValue = 0,
        MaxValue = 1,
        TranslatesAutoresizingMaskIntoConstraints = false
    };

    private readonly NSButton _rosetta = LauncherStyle.Button("Install Rosetta…");
    private readonly NSTextField _source = LauncherStyle.Label("", 12, NSFontWeight.Regular);

    private readonly NSTextField _status = LauncherStyle.Label("", 12, NSFontWeight.Regular,
        LauncherStyle.SecondaryText);

    private readonly NSTextField _step = LauncherStyle.Label("1 · Apple graphics", 14, NSFontWeight.Semibold);
    private readonly NSWindow _window;
    private string? _appleDownload;
    private bool _busy;
    private bool _disposed;
    private GameLaunchConfiguration? _ready;
    private readonly bool _testMode;

    private RuntimeSetupWizard(NSWindow owner, IRuntimeSetup installer, bool testMode, CancellationToken token)
    {
        _testMode = testMode;
        _owner = owner;
        _installer = installer;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _window = new NSWindow(new CGRect(0, 0, 600, 440), NSWindowStyle.Titled,
            NSBackingStore.Buffered, false)
        {
            Title = "Set up DeadLocky",
            BackgroundColor = LauncherStyle.Background,
            Appearance = NSAppearance.GetAppearance(NSAppearance.NameDarkAqua)
        };
        NSView content = _window.ContentView!;
        NSTextField title = LauncherStyle.Label("Make your Mac game-ready.", 25, NSFontWeight.Semibold);
        NSTextField introduction = LauncherStyle.Label(
            "Choose your Apple GPTK download once. We'll prepare your Mac for Deadlock "
            + "and install Windows Steam automatically.", 13, NSFontWeight.Regular,
            LauncherStyle.SecondaryText);
        Wrap(introduction);
        Wrap(_source);
        Wrap(_status);
        foreach (NSView view in new NSView[]
                 {
                     title, introduction, _step, _source, _download, _choose, _progress, _status,
                     _rosetta, _install, _cancel
                 })
        {
            content.AddSubview(view);
        }

        NSLayoutConstraint.ActivateConstraints(
        [
            title.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, 28),
            title.TopAnchor.ConstraintEqualTo(content.TopAnchor, 28),
            introduction.LeadingAnchor.ConstraintEqualTo(title.LeadingAnchor),
            introduction.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor, -28),
            introduction.WidthAnchor.ConstraintEqualTo(544),
            introduction.TopAnchor.ConstraintEqualTo(title.BottomAnchor, 14),
            _step.LeadingAnchor.ConstraintEqualTo(title.LeadingAnchor),
            _step.TopAnchor.ConstraintEqualTo(content.TopAnchor, 140),
            _source.LeadingAnchor.ConstraintEqualTo(title.LeadingAnchor),
            _source.TrailingAnchor.ConstraintEqualTo(introduction.TrailingAnchor),
            _source.TopAnchor.ConstraintEqualTo(_step.BottomAnchor, 12),
            _source.HeightAnchor.ConstraintEqualTo(34),
            _download.LeadingAnchor.ConstraintEqualTo(title.LeadingAnchor),
            _download.TopAnchor.ConstraintEqualTo(_source.BottomAnchor, 10),
            _choose.LeadingAnchor.ConstraintEqualTo(_download.TrailingAnchor, 10),
            _choose.CenterYAnchor.ConstraintEqualTo(_download.CenterYAnchor),
            _progress.LeadingAnchor.ConstraintEqualTo(title.LeadingAnchor),
            _progress.TrailingAnchor.ConstraintEqualTo(introduction.TrailingAnchor),
            _progress.TopAnchor.ConstraintEqualTo(_download.BottomAnchor, 24),
            _status.LeadingAnchor.ConstraintEqualTo(title.LeadingAnchor),
            _status.TrailingAnchor.ConstraintEqualTo(introduction.TrailingAnchor),
            _status.TopAnchor.ConstraintEqualTo(_progress.BottomAnchor, 12),
            _status.HeightAnchor.ConstraintEqualTo(54),
            _rosetta.LeadingAnchor.ConstraintEqualTo(title.LeadingAnchor),
            _rosetta.BottomAnchor.ConstraintEqualTo(content.BottomAnchor, -24),
            _install.TrailingAnchor.ConstraintEqualTo(introduction.TrailingAnchor),
            _install.BottomAnchor.ConstraintEqualTo(_rosetta.BottomAnchor),
            _cancel.TrailingAnchor.ConstraintEqualTo(_install.LeadingAnchor, -10),
            _cancel.CenterYAnchor.ConstraintEqualTo(_install.CenterYAnchor)
        ]);
        _ready = installer.FindExisting();
        _appleDownload = installer.FindAppleDownload();
        _source.StringValue = _ready is not null ? "Your installed environment is ready. Nothing to download."
            : _appleDownload is not null ? "Apple graphics found in your mounted evaluation environment."
            : "Download GPTK from Apple, then select its DMG. Review Apple's terms with your download.";
        _status.StringValue = "1  Apple graphics    →    2  Environment & Steam    →    3  Sign in";
        _install.Enabled = _ready is not null || _appleDownload is not null;
        _install.Title = _ready is null ? "Set up environment" : "Continue to Steam";
        _step.StringValue = _ready is null ? "1 · Apple graphics" : "Your Mac is ready";
        _download.Hidden = _choose.Hidden = _ready is not null;
        _progress.Hidden = true;
        _rosetta.Hidden = true;
        _download.Activated += OnDownload;
        _choose.Activated += OnChoose;
        _install.Activated += OnInstall;
        _cancel.Activated += OnCancel;
        _rosetta.Activated += OnRosetta;
    }

    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _download.Activated -= OnDownload;
        _choose.Activated -= OnChoose;
        _install.Activated -= OnInstall;
        _cancel.Activated -= OnCancel;
        _rosetta.Activated -= OnRosetta;
        _window.Dispose();
    }

    public static async Task<GameLaunchConfiguration?> ShowAsync(NSWindow owner, IRuntimeSetup installer,
        CancellationToken token)
    {
        using var wizard = new RuntimeSetupWizard(owner, installer, false, token);
        return await wizard.ShowSheetAsync(token);
    }

#if DEBUG
    public static async Task ShowTestAsync(NSWindow owner, IRuntimeSetup installer,
        CancellationToken token)
    {
        using var wizard = new RuntimeSetupWizard(owner, installer, true, token);
        wizard._window.Title = "Test environment setup";
        wizard._step.StringValue = "1 · Clean setup test";
        wizard._status.StringValue = wizard._appleDownload is null
            ? "Choose Apple download…, then Set up environment. Your game and Steam sign-in stay untouched."
            : "Click Set up environment to begin a clean installation. Your game and Steam sign-in stay untouched.";
        _ = await wizard.ShowSheetAsync(token);
    }
#endif

    private async Task<GameLaunchConfiguration?> ShowSheetAsync(CancellationToken token)
    {
        _owner.BeginSheet(_window, static _ => { });
        await using CancellationTokenRegistration registration = token.Register(OnOwnerCancelled);
        return await _completion.Task;
    }

    private void OnOwnerCancelled()
    {
        _owner.BeginInvokeOnMainThread(() =>
        {
            if (!_disposed)
            {
                Cancel();
            }
        });
    }

    private static void Wrap(NSTextField field)
    {
        field.UsesSingleLineMode = false;
        field.MaximumNumberOfLines = 3;
        field.LineBreakMode = NSLineBreakMode.ByWordWrapping;
        field.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
    }

    private static void OnDownload(object? sender, EventArgs args)
    {
        _ = NSWorkspace.SharedWorkspace.OpenUrl(new NSUrl("https://developer.apple.com/games/game-porting-toolkit/"));
    }

    private void OnChoose(object? sender, EventArgs args)
    {
        _ = UiAction.RunAsync(_window, OnChooseAsync);
    }

    private async Task OnChooseAsync()
    {
        using NSOpenPanel panel = NSOpenPanel.OpenPanel;
        panel.Message = "Choose your Apple GPTK DMG or mounted evaluation environment";
        panel.CanChooseFiles = true;
        panel.CanChooseDirectories = true;
        panel.AllowsMultipleSelection = false;
        var completion = new TaskCompletionSource<nint>();
        panel.BeginSheet(_window, response => completion.TrySetResult(response));
        nint response = await completion.Task;
        if (response != (nint)NSModalResponse.OK || _disposed)
        {
            return;
        }

        _appleDownload = panel.Url.Path;
        _source.StringValue = "Selected: " + Path.GetFileName(_appleDownload);
        _install.Enabled = true;
    }

    private void OnInstall(object? sender, EventArgs args)
    {
        _ = UiAction.RunAsync(_window, OnInstallAsync);
    }

    private async Task OnInstallAsync()
    {
        if (_busy)
        {
            return;
        }

        if (_ready is not null)
        {
            Finish(_ready);
            return;
        }

        if (_appleDownload is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var progress = new Progress<RuntimeSetupProgress>(value => _window.BeginInvokeOnMainThread(() =>
            {
                if (_disposed)
                {
                    return;
                }

                _step.StringValue = value.Step == "Ready"
                    ? _testMode ? "3 · Test complete" : "3 · Sign in to Steam"
                    : "2 · Environment & Steam";
                _status.StringValue = value.Message;
                _progress.Indeterminate = value.Fraction is null;
                if (value.Fraction is { } fraction)
                {
                    _progress.DoubleValue = fraction;
                }
                else
                {
                    _progress.StartAnimation(_progress);
                }
            }));
            _ready = await Task.Run(() => _installer.InstallAsync(_appleDownload, progress, _lifetime.Token));
            _step.StringValue = _testMode ? "3 · Test complete" : "3 · Sign in to Steam";
            _status.StringValue = _testMode
                ? "Setup succeeded in a separate environment. Your game and saved settings are unchanged."
                : "All set. Continue, then sign in to Windows Steam once to download and play.";
            _source.StringValue = "Apple graphics, Wine and Windows Steam are ready.";
            _install.Title = _testMode ? "Finish test" : "Continue to Steam";
        });
    }

    private void OnRosetta(object? sender, EventArgs args)
    {
        _ = UiAction.RunAsync(_window, OnRosettaAsync);
    }

    private async Task OnRosettaAsync()
    {
        using var alert = new NSAlert();
        alert.MessageText = "Install Rosetta from Apple?";
        alert.InformativeText = "Rosetta runs the Intel game environment on your Mac. Choosing Install accepts "
                                + "Apple's Rosetta software license. You can review the license before continuing.";
        LauncherStyle.ApplyButtonStyle(alert.AddButton("Install"));
        LauncherStyle.ApplyButtonStyle(alert.AddButton("Cancel"));
        LauncherStyle.ApplyButtonStyle(alert.AddButton("Review license"));
        nint choice = alert.RunSheetModal(_window);
        if (choice == 1002)
        {
            _ = NSWorkspace.SharedWorkspace.OpenUrl(new NSUrl("https://www.apple.com/legal/sla/"));
            return;
        }

        if (choice != 1000)
        {
            return;
        }

        await RunAsync(async () =>
        {
            _status.StringValue = "Installing Rosetta…";
            await Task.Run(() => _installer.InstallRosettaAsync(_lifetime.Token));
            _status.StringValue = "Rosetta is ready. Retry Set up environment.";
            _rosetta.Hidden = true;
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        _busy = true;
        _install.Enabled = _choose.Enabled = _download.Enabled = _rosetta.Enabled = false;
        _progress.Hidden = false;
        _progress.Indeterminate = true;
        _progress.StartAnimation(_progress);
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            if (_lifetime.IsCancellationRequested)
            {
                Finish(null);
            }
            else
            {
                _status.StringValue = "Setup timed out. Your downloads are cached; retry to continue.";
            }
        }
        catch (Exception exception)
        {
            _status.StringValue = exception.Message;
            _status.ToolTip = exception.Message;
            _install.Title = "Retry setup";
            _rosetta.Hidden = !exception.Message.StartsWith("Rosetta is required", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _busy = false;
            if (!_disposed && !_completion.Task.IsCompleted)
            {
                _progress.StopAnimation(_progress);
                _install.Enabled = _choose.Enabled = _download.Enabled = _rosetta.Enabled = true;
            }
        }
    }

    private void OnCancel(object? sender, EventArgs args)
    {
        Cancel();
    }

    private void Cancel()
    {
        _lifetime.Cancel();
        if (!_busy)
        {
            Finish(null);
        }
        else
        {
            _cancel.Enabled = false;
            _status.StringValue = "Stopping setup and cleaning up…";
        }
    }

    private void Finish(GameLaunchConfiguration? configuration)
    {
        _owner.EndSheet(_window);
        _window.OrderOut(null);
        _ = _completion.TrySetResult(configuration);
    }
}
