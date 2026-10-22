using DeadLocky.Entities.Game.Model;
using DeadLocky.Entities.Steam.Model;
using DeadLocky.Features.PrepareGame.Api;
using DeadLocky.Shared.Api.Diagnostics;
using DeadLocky.Shared.Api.Directories;

namespace DeadLocky.Features.PrepareGame.Model;

internal sealed class GamePreparationController : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IDirectoryPicker _picker;
    private readonly IGamePreparationService _service;
    private readonly Lock _sync = new();
    private bool _disposed;
    private CancellationTokenSource? _operation;
    private GamePreparationState _state;

    public GamePreparationController(IGamePreparationService service, IDirectoryPicker picker)
    {
        _service = service;
        _picker = picker;
        _state = new GamePreparationState(service.ReadDirectory());
        _service.SessionLost += OnSessionLost;
    }

    public GamePreparationState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StateChanged = null;
        }

        _service.SessionLost -= OnSessionLost;
        _lifetime.Cancel();
        _service.Dispose();
        _lifetime.Dispose();
    }

    public event EventHandler? StateChanged;

    public Task InitializeAsync()
    {
        return RunAsync(PreparationAction.Checking, async token =>
        {
            bool downloaded = await _service.IsDownloadedAsync(State.Directory, token);
            token.ThrowIfCancellationRequested();
            Update(state => state with { IsDownloaded = downloaded });
            SteamAccount? account = await _service.RestoreSessionAsync(token);
            token.ThrowIfCancellationRequested();
            Update(state => state with { Account = account });
        });
    }

    public Task SignInAsync()
    {
        return RunAsync(PreparationAction.SigningIn, async token =>
        {
            SteamAccount account = await _service.RestoreSessionAsync(token)
                                   ?? await _service.SignInAsync(token);
            token.ThrowIfCancellationRequested();
            Update(state => state with { Account = account });
        });
    }

    public Task ChooseDirectoryAsync()
    {
        return RunAsync(PreparationAction.ChoosingFolder, async token =>
        {
            string? path = await _picker.PickAsync(token);
            token.ThrowIfCancellationRequested();
            if (path is null)
            {
                return;
            }

            var directory = new GameDirectory(Path.GetFullPath(path));
            _service.SaveDirectory(directory);
            Update(state => state with { Directory = directory, IsDownloaded = false });
            bool downloaded = await _service.IsDownloadedAsync(directory, token);
            token.ThrowIfCancellationRequested();
            Update(state => state with { IsDownloaded = downloaded });
        });
    }

    public Task DownloadAsync()
    {
        return RunAsync(PreparationAction.Downloading, async token =>
        {
            if (State.Account is null)
            {
                throw new InvalidOperationException("Sign in to Steam before downloading Deadlock.");
            }

            Update(static state => state with { IsDownloaded = false });
            var progress = new ImmediateProgress<GameDownloadProgress>(value =>
                Update(state => state with { Progress = value }));
            try
            {
                await _service.DownloadAsync(State.Directory, progress, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested
                                                     && !_lifetime.IsCancellationRequested)
            {
                bool stillInstalled = await _service.IsDownloadedAsync(State.Directory, _lifetime.Token);
                Update(state => state with { IsDownloaded = stillInstalled });
                throw;
            }

            bool downloaded = await _service.IsDownloadedAsync(State.Directory, token);
            token.ThrowIfCancellationRequested();
            if (!downloaded)
            {
                throw new IOException("The download could not be verified. Please retry.");
            }

            Update(static state => state with { IsDownloaded = true, Progress = null });
        });
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _ = _operation?.CancelAsync();
        }
    }

    private async Task RunAsync(PreparationAction action, Func<CancellationToken, Task> operation)
    {
        CancellationTokenSource cancellation;
        lock (_sync)
        {
            if (_disposed || _state.IsBusy)
            {
                return;
            }

            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _operation = cancellation;
            _state = _state with { Action = action, Error = null, Progress = null };
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await AppLog.RunAsync("game.preparation." + action, () => operation(cancellation.Token));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Update(state => state with { Error = exception.Message });
        }
        finally
        {
            lock (_sync)
            {
                _operation = null;
            }

            cancellation.Dispose();
            Update(static state => state with { Action = PreparationAction.None, Progress = null });
        }
    }

    private void OnSessionLost(object? sender, EventArgs args)
    {
        Update(static state => state with { Account = null, Error = "Steam disconnected. Please sign in again." });
        Cancel();
    }

    private void Update(Func<GamePreparationState, GamePreparationState> update)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                AppLog.Event("game.preparation.result", "discarded after disposal");
                return;
            }

            _state = update(_state);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
