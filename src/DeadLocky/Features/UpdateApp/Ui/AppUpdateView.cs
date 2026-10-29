using DeadLocky.Features.UpdateApp.Api;
using DeadLocky.Features.UpdateApp.Model;
using DeadLocky.Shared.Api.Diagnostics;
using DeadLocky.Shared.Ui;

namespace DeadLocky.Features.UpdateApp.Ui;

public sealed class AppUpdateView : NSStackView
{
    private readonly NSButton _button = LauncherStyle.IconButton("arrow.triangle.2.circlepath",
        "Check for updates and install the latest release");

    private readonly AppUpdateClient _client;
    private readonly HttpClient _http;
    private readonly MacUpdateInstaller _installer;
    private readonly CancellationTokenSource _lifetime = new();

    private readonly NSTextField _status = LauncherStyle.Label("", 11, NSFontWeight.Regular,
        LauncherStyle.SecondaryText);

    private readonly AppVersion _version;
    private bool _disposed;
    private bool _gameBusy;
    private TaskCompletionSource? _idle;
    private Task _operation = Task.CompletedTask;
    private bool _startupChecked;

    public AppUpdateView(HttpClient http, MacUpdateInstaller installer, AppVersion version)
    {
        _http = http;
        _client = new AppUpdateClient(http);
        _installer = installer;
        _version = version;
        Orientation = NSUserInterfaceLayoutOrientation.Horizontal;
        Alignment = NSLayoutAttribute.CenterY;
        Spacing = 8;
        TranslatesAutoresizingMaskIntoConstraints = false;
        _status.MaximumNumberOfLines = 1;
        _status.LineBreakMode = NSLineBreakMode.TruncatingTail;
        _status.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        AddArrangedSubview(_status);
        AddArrangedSubview(_button);
        _button.Activated += OnUpdate;
    }

    public bool IsBusy { get; private set; }
    public bool HasOperation { get; private set; }

    public Task ConfirmLaunchAsync()
    {
        return _installer.ConfirmLaunchAsync(_version);
    }

    public event EventHandler? BusyChanged;

    public void SetGameBusy(bool busy)
    {
        _gameBusy = busy;
        _button.Enabled = !busy && !HasOperation;
        if (!busy)
        {
            _ = _idle?.TrySetResult();
        }

        _button.ToolTip = busy
            ? "Finish the current game or installation before updating DeadLocky."
            : $"DeadLocky {_version} · Check for updates and install the latest release";
    }

    private void OnUpdate(object? sender, EventArgs args)
    {
        StartCheck(false);
    }

    public async Task StopAsync()
    {
        await _lifetime.CancelAsync();
        await _operation;
    }

    public void CheckAutomatically()
    {
        if (_startupChecked || _disposed)
        {
            return;
        }

        _startupChecked = true;
        StartCheck(true);
    }

    private void StartCheck(bool automatic)
    {
        if (HasOperation || _disposed)
        {
            return;
        }

        HasOperation = true;
        _button.Enabled = false;
        _operation = automatic ? CheckAsync(true) : UiAction.RunAsync(Window, () => CheckAsync(false));
    }

    private async Task CheckAsync(bool automatic)
    {
        try
        {
            SetStatus("Checking GitHub…");
            using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            checkTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            AppRelease? release = await _client.FindUpdateAsync(_version, checkTimeout.Token);
            if (release is null)
            {
                AppLog.Event("update.check.result", "up to date");
                SetStatus($"{_version} · Up to date", "checkmark.circle");
                return;
            }

            AppLog.Event("update.check.result", "newer release available");
            if (_gameBusy)
            {
                AppLog.Event("update.install", "deferred while application is busy");
            }

            while (_gameBusy)
            {
                SetStatus($"{release.Version} ready · Installs when idle", "arrow.down.circle");
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await _idle.Task.WaitAsync(_lifetime.Token);
            }

            _idle = null;
            _lifetime.Token.ThrowIfCancellationRequested();
#if DEBUG
            SetStatus($"{release.Version} available · Use an installed Release build", "arrow.down.circle");
#else
            IsBusy = true;
            BusyChanged?.Invoke(this, EventArgs.Empty);
            var progress = new Progress<double>(value =>
            {
                if (!_disposed)
                {
                    SetStatus($"Downloading {release.Version} · {value:P0}");
                }
            });
            SetStatus($"Downloading {release.Version}…");
            using PreparedAppUpdate update = await _installer.PrepareAsync(_client, release, progress, _lifetime.Token);
            SetStatus("Installing · Restarting DeadLocky…");
            await update.StartInstallerAsync(Environment.ProcessId, _lifetime.Token);

            BeginInvokeOnMainThread(() => NSApplication.SharedApplication.Terminate(this));
#endif
        }
        catch (OperationCanceledException)
        {
            AppLog.Event("update.ui", "cancelled or timed out");
            if (!_lifetime.IsCancellationRequested)
            {
                SetStatus("Update check timed out", "exclamationmark.circle");
            }
        }
        catch (Exception exception)
        {
            AppLog.Event("update.ui", "failed", exception);
            if (!_disposed)
            {
                SetStatus("Update failed · Click to retry", "exclamationmark.circle");
                _status.ToolTip = exception.Message;
                _status.TextColor = NSColor.SystemRed;
                if (!automatic)
                {
                    throw;
                }
            }
        }
        finally
        {
            IsBusy = false;
            HasOperation = false;
            _idle = null;
            if (!_disposed)
            {
                _button.Enabled = !_gameBusy;
                BusyChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void SetStatus(string message, string symbol = "arrow.triangle.2.circlepath")
    {
        if (_disposed)
        {
            return;
        }

        _status.StringValue = message;
        _status.ToolTip = message;
        _status.TextColor = LauncherStyle.SecondaryText;
        _button.Image = NSImage.GetSystemSymbol(symbol, null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _button.Activated -= OnUpdate;
            _lifetime.Cancel();
            _http.Dispose();
            _lifetime.Dispose();
            BusyChanged = null;
        }

        base.Dispose(disposing);
    }
}
