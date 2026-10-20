using DeadLocky.Features.PrepareGame.Api;
using DeadLocky.Features.PrepareGame.Model;
using DeadLocky.Shared.Api.Directories;
using DeadLocky.Shared.Ui;

namespace DeadLocky.Features.PrepareGame.Ui;

public sealed class GamePreparationView : NSStackView
{
    private readonly GamePreparationController _controller;

    private readonly NSTextField _error = LauncherStyle.Label(string.Empty, 12, NSFontWeight.Regular,
        NSColor.SystemRed);

    private readonly PreparationRow _folder = new("Install location", "Change folder…");
    private readonly PreparationRow _game = new("Deadlock", "Download", true);

    private readonly NSTextField _message = LauncherStyle.Label(string.Empty, 11, NSFontWeight.Regular,
        LauncherStyle.SecondaryText);

    private readonly NSProgressIndicator _progress = new()
    {
        Style = NSProgressIndicatorStyle.Bar,
        MinValue = 0,
        MaxValue = 1,
        TranslatesAutoresizingMaskIntoConstraints = false
    };

    private readonly PreparationRow _steam = new("Steam account", "Sign in");
    private bool _disposed;
    private bool _gameRunning;
    private bool _externalBusy;
    private Func<Task>? _openSteamAccount;
    private Func<Task<bool>>? _prepareSteamRuntime;

    public GamePreparationView(IDirectoryPicker picker, IGamePreparationService service)
    {
        _controller = new GamePreparationController(service, picker);
        Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        Alignment = NSLayoutAttribute.Leading;
        Spacing = 12;
        TranslatesAutoresizingMaskIntoConstraints = false;
        _folder.Detail.Selectable = true;
        _folder.Button.KeyEquivalent = "o";
        _folder.Button.KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask;
        _message.LineBreakMode = NSLineBreakMode.TruncatingMiddle;
        _message.UsesSingleLineMode = true;
        _error.MaximumNumberOfLines = 2;
        _error.UsesSingleLineMode = false;
        _error.LineBreakMode = NSLineBreakMode.ByWordWrapping;
        var content = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        NSView surface = LauncherStyle.Surface(content);
        foreach (NSView view in new NSView[] { _steam, _folder, _game, _progress, _message })
        {
            content.AddSubview(view);
        }

        AddArrangedSubview(surface);
        AddArrangedSubview(_error);
        NSLayoutConstraint.ActivateConstraints(
        [
            surface.WidthAnchor.ConstraintEqualTo(WidthAnchor),
            surface.HeightAnchor.ConstraintEqualTo(324),
            _steam.TopAnchor.ConstraintEqualTo(content.TopAnchor),
            _folder.TopAnchor.ConstraintEqualTo(_steam.BottomAnchor),
            _game.TopAnchor.ConstraintEqualTo(_folder.BottomAnchor),
            _steam.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            _steam.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            _folder.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            _folder.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            _game.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            _game.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            _progress.TopAnchor.ConstraintEqualTo(_game.BottomAnchor, 4),
            _progress.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, 24),
            _progress.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor, -24),
            _progress.HeightAnchor.ConstraintEqualTo(4),
            _message.LeadingAnchor.ConstraintEqualTo(_progress.LeadingAnchor),
            _message.TrailingAnchor.ConstraintEqualTo(_progress.TrailingAnchor),
            _message.TopAnchor.ConstraintEqualTo(_progress.BottomAnchor, 12),
            _error.WidthAnchor.ConstraintEqualTo(WidthAnchor)
        ]);
        _steam.Button.Activated += OnSteam;
        _folder.Button.Activated += OnFolder;
        _game.Button.Activated += OnDownload;
        _controller.StateChanged += OnStateChanged;
        Render();
        _ = _controller.InitializeAsync();
    }

    public string DirectoryPath => _controller.State.Directory.Path;
    public bool IsBusy => _controller.State.IsBusy;
    public bool IsDownloaded => _controller.State.IsDownloaded;

    public bool IsInstallationBusy => _controller.State.Action
        is PreparationAction.Downloading or PreparationAction.ChoosingFolder;

    public void SetRuntimeSetup(Func<Task<bool>> prepare)
    {
        _prepareSteamRuntime = prepare;
    }

    public void UseSteamClient(Func<Task> openSteamAccount)
    {
        _openSteamAccount = openSteamAccount;
        Render();
    }

    public Task RefreshSteamClientAsync()
    {
        return _openSteamAccount is null
            ? Task.CompletedTask
            : _controller.InitializeAsync();
    }

    public event EventHandler? StateChanged;

    public void SetGameRunning(bool running)
    {
        _gameRunning = running;
        Render();
    }

    public void SetExternalBusy(bool busy)
    {
        _externalBusy = busy;
        Render();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _steam.Button.Activated -= OnSteam;
            _folder.Button.Activated -= OnFolder;
            _game.Button.Activated -= OnDownload;
            _controller.StateChanged -= OnStateChanged;
            _controller.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnSteam(object? sender, EventArgs args)
    {
        _ = UiAction.RunAsync(Window, OnSteamAsync);
    }

    private async Task OnSteamAsync()
    {
        if (_gameRunning || _externalBusy)
        {
            return;
        }

        if (_controller.State.IsBusy)
        {
            _controller.Cancel();
        }
        else if (_controller.State.Account is null)
        {
            if (_prepareSteamRuntime is not null && !await _prepareSteamRuntime())
            {
                return;
            }

            await _controller.SignInAsync();
        }
        else
        {
            if (_openSteamAccount is null)
            {
                return;
            }

            await _openSteamAccount();
            await _controller.InitializeAsync();
        }
    }

    private void OnFolder(object? sender, EventArgs args)
    {
        _ = UiAction.RunAsync(Window, OnFolderAsync);
    }

    private async Task OnFolderAsync()
    {
        if (!_gameRunning)
        {
            await _controller.ChooseDirectoryAsync();
        }
    }

    private void OnDownload(object? sender, EventArgs args)
    {
        _ = UiAction.RunAsync(Window, OnDownloadAsync);
    }

    private async Task OnDownloadAsync()
    {
        if (_gameRunning || _controller.State.IsDownloaded)
        {
            return;
        }

        if (_controller.State.Action == PreparationAction.Downloading)
        {
            _controller.Cancel();
        }
        else
        {
            await _controller.DownloadAsync();
        }
    }

    private void OnStateChanged(object? sender, EventArgs args)
    {
        BeginInvokeOnMainThread(() =>
        {
            Render();
            if (!_disposed)
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private void Render()
    {
        if (_disposed)
        {
            return;
        }

        GamePreparationState state = _controller.State;
        _steam.SetCompleted(state.Account is not null);
        _folder.SetCompleted(true);
        _game.SetCompleted(state.IsDownloaded);
        _steam.Detail.StringValue = state.Account is null
            ? "Sign in once in Windows Steam"
            : "Saved Steam sign-in: " + state.Account.Name;
        _folder.Detail.StringValue = state.Directory.Path;
        _folder.Detail.ToolTip = state.Directory.Path;
        _game.Detail.StringValue = state.IsDownloaded
            ? "Downloaded · Windows 64-bit"
            : state.Action == PreparationAction.Downloading
                ? "Downloading and verifying…"
                : "Ready to download · Windows 64-bit";
        bool signingIn = state.Action is PreparationAction.SigningIn or PreparationAction.Checking;
        _steam.Button.Title = signingIn ? "Cancel"
            : state.Account is null ? "Sign in"
            : "Manage Steam";
        _steam.Button.Enabled = !_gameRunning && !_externalBusy && (!state.IsBusy || signingIn);
        _folder.Button.Enabled = !_gameRunning && !_externalBusy && !state.IsBusy;
        _game.Button.Title = state.Action == PreparationAction.Downloading
            ? "Stop waiting"
            : "Download";
        _game.Button.Hidden = state.IsDownloaded;
        _game.Button.Enabled = !_gameRunning && !_externalBusy && !state.IsDownloaded && (state.Action == PreparationAction.Downloading
                                                                        || state is
                                                                        {
                                                                            IsBusy: false, Account: not null
                                                                        });
        _message.StringValue = state.Progress is { } download
            ? FormatProgress(download)
            : state.Action == PreparationAction.Checking
                ? "Checking your installation and saved Steam session…"
                : state.Action == PreparationAction.SigningIn
                    ? "Finish signing in in Steam and enable Remember me."
                    : state.IsDownloaded
                        ? "Game files found. Steam manages your sign-in and updates."
                        : "Your Steam account must have access to Deadlock.";
        _progress.Hidden = state.Action != PreparationAction.Downloading;
        _progress.Indeterminate = state.Progress?.Fraction is null;
        _progress.DoubleValue = state.Progress?.Fraction ?? 0;
        if (_progress is { Hidden: false, Indeterminate: true })
        {
            _progress.StartAnimation(this);
        }
        else
        {
            _progress.StopAnimation(this);
        }

        _error.StringValue = state.Error ?? string.Empty;
        _error.ToolTip = state.Error;
        _error.Hidden = state.Error is null;
    }

    private static string FormatProgress(GameDownloadProgress progress)
    {
        if (progress.Fraction is not { } fraction)
        {
            return progress.Message;
        }

        const double gigabyte = 1024d * 1024 * 1024;
        return $"{fraction:P0} · {progress.CompletedBytes / gigabyte:F1} / {progress.TotalBytes / gigabyte:F1} GB"
               + " · " + progress.Message;
    }
}
