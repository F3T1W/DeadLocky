using DeadLocky.Features.LaunchGame.Api;
using DeadLocky.Shared.Api.Diagnostics;

namespace DeadLocky.Features.LaunchGame.Model;

internal sealed class GameLaunchController(IGameLauncher launcher) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _sync = new();
    private bool _disposed;
    private IGameSession? _session;
    private GameLaunchState _state = new();

    public GameLaunchState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public GameLaunchConfiguration? Configuration => launcher.Configuration;

    public bool CanEmergencyStop => launcher is IEmergencyGameStopper;

    public bool CloseSteamAfterExit => launcher is IGameExitPreferences { CloseSteamAfterExit: true };

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

        _lifetime.Cancel();
        _session?.Dispose();
        launcher.Dispose();
    }

    public async Task<bool> EmergencyStopAsync()
    {
        lock (_sync)
        {
            if (_disposed || _state.IsStopping || _state.IsConfiguring || launcher is not IEmergencyGameStopper)
            {
                return false;
            }

            _state = _state with { EmergencyStopRequested = true, IsStopping = true, Error = null };
            _session?.Dispose();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await ((IEmergencyGameStopper)launcher).StopAsync(_lifetime.Token);
            return true;
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return false;
        }
        catch (Exception exception)
        {
            AppLog.Event("game.controller", "failed", exception);
            Update(state => state with { IsStopping = false, Error = exception.Message });
            return false;
        }
    }

    public void SetCloseSteamAfterExit(bool enabled)
    {
        try
        {
            if (launcher is IGameExitPreferences preferences)
            {
                preferences.CloseSteamAfterExit = enabled;
            }

            Update(static state => state with { Error = null });
        }
        catch (Exception exception)
        {
            AppLog.Event("game.controller", "failed", exception);
            Update(state => state with { Error = exception.Message });
        }
    }

    public event EventHandler? StateChanged;

    public void SetInstallation(string directory, bool ready, bool busy)
    {
        Update(state => state with { Directory = directory, InstallationReady = ready, PreparationBusy = busy });
    }

    public async Task CheckRunningAsync()
    {
        if (State.IsRunning || State.IsStarting || State.IsConfiguring || State.EmergencyStopRequested
            || string.IsNullOrEmpty(State.Directory))
        {
            return;
        }

        try
        {
            IGameSession? existing = await launcher.FindRunningAsync(State.Directory, _lifetime.Token);
            if (existing is not null)
            {
                await ObserveAsync(existing);
            }
        }
        catch (OperationCanceledException) when (_disposed || State.EmergencyStopRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Event("game.controller", "failed", exception);
            Update(state => state with { Error = exception.Message });
        }
    }

    public async Task LaunchAsync()
    {
        lock (_sync)
        {
            if (_disposed || !_state.CanLaunch)
            {
                return;
            }

            _state = _state with { IsStarting = true, Error = null };
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            IGameSession session = await launcher.LaunchAsync(State.Directory, _lifetime.Token);
            await ObserveAsync(session);
        }
        catch (OperationCanceledException) when (_disposed || State.EmergencyStopRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Event("game.controller", "failed", exception);
            Update(state => state with { Error = exception.Message });
        }
        finally
        {
            Update(static state => state with { IsStarting = false });
        }
    }

    public async Task ConfigureAsync(Func<CancellationToken, Task<GameLaunchConfiguration?>> choose)
    {
        lock (_sync)
        {
            if (_disposed || _state.IsStarting || _state.IsRunning || _state.IsConfiguring
                || _state.EmergencyStopRequested)
            {
                return;
            }

            _state = _state with { IsConfiguring = true, Error = null };
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            GameLaunchConfiguration? configuration = await choose(_lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            if (configuration is not null)
            {
                launcher.Configure(configuration);
            }
        }
        catch (OperationCanceledException) when (_disposed || State.EmergencyStopRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Event("game.controller", "failed", exception);
            Update(state => state with { Error = exception.Message });
        }
        finally
        {
            Update(static state => state with { IsConfiguring = false });
        }
    }

    public async Task OpenSteamAsync()
    {
        try
        {
            if (State is { IsRunning: false, IsStarting: false, EmergencyStopRequested: false })
            {
                await launcher.OpenSteamAsync(_lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_disposed || State.EmergencyStopRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Event("game.controller", "failed", exception);
            Update(state => state with { Error = exception.Message });
        }
    }

    private async Task ObserveAsync(IGameSession session)
    {
        lock (_sync)
        {
            if (_disposed || _state.EmergencyStopRequested || _session is not null)
            {
                AppLog.Event("game.session.result", "discarded stale session");
                session.Dispose();
                return;
            }

            _session = session;
            _state = _state with { IsStarting = false, IsRunning = true, Error = null };
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await AppLog.RunAsync("game.session", () => session.Completion.WaitAsync(_lifetime.Token));
        }
        finally
        {
            lock (_sync)
            {
                _session = null;
            }

            session.Dispose();
            Update(static state => state with { IsRunning = false });
        }
    }

    private void Update(Func<GameLaunchState, GameLaunchState> update)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _state = update(_state);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
