using DeadLocky.Features.LaunchGame.Api;
using DeadLocky.Features.LaunchGame.Model;

namespace DeadLocky.UnitTests;

public sealed class EmergencyStopTests
{
    [Fact]
    public async Task ShutdownDetachesSessionAndBlocksNewActionsBeforeStoppingPrefix()
    {
        var launcher = new EmergencyLauncher();
        using var controller = new GameLaunchController(launcher);
        controller.SetInstallation("/game", true, false);
        Task launch = controller.LaunchAsync();
        Assert.True(controller.State.IsRunning);

        Task<bool> stop = controller.EmergencyStopAsync();
        Assert.True(launcher.Session.Disposed);
        Assert.True(controller.State.IsStopping);
        Assert.False(controller.State.CanLaunch);
        Assert.False(await controller.EmergencyStopAsync());
        await controller.LaunchAsync();
        await controller.OpenSteamAsync();
        await controller.CheckRunningAsync();
        await controller.ConfigureAsync(static _ =>
            throw new InvalidOperationException("Must not configure during shutdown."));
        Assert.Equal(1, launcher.Starts);
        Assert.Equal(0, launcher.SteamStarts);
        Assert.Equal(0, launcher.Scans);
        launcher.StopCompletion.SetResult();
        Assert.True(await stop);
        launcher.Session.Exit.SetCanceled();
        await launch;
        Assert.Null(controller.State.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedShutdownReportsErrorAndAllowsOnlyShutdownRetry(bool cancelled)
    {
        var launcher = new EmergencyLauncher
        {
            StopError = cancelled
                ? new OperationCanceledException("Unable to stop prefix before timeout.")
                : new IOException("Unable to stop prefix.")
        };
        using var controller = new GameLaunchController(launcher);
        controller.SetInstallation("/game", true, false);
        Assert.False(await controller.EmergencyStopAsync());
        Assert.Contains("Unable to stop", controller.State.Error);
        Assert.False(controller.State.IsStopping);
        Assert.False(controller.State.CanLaunch);
        launcher.StopError = null;
        launcher.StopCompletion.SetResult();
        Assert.True(await controller.EmergencyStopAsync());
        Assert.Null(controller.State.Error);
    }

    [Fact]
    public async Task ShutdownDoesNotRequireAReadyInstallationOrRunningGame()
    {
        var launcher = new EmergencyLauncher();
        launcher.StopCompletion.SetResult();
        using var controller = new GameLaunchController(launcher);
        Assert.True(await controller.EmergencyStopAsync());
        Assert.Equal(0, launcher.Starts);
    }

    private sealed class EmergencyLauncher : IGameLauncher, IEmergencyGameStopper
    {
        public EmergencySession Session { get; } = new();
        public TaskCompletionSource StopCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? StopError { get; set; }
        public int Starts { get; private set; }
        public int SteamStarts { get; private set; }
        public int Scans { get; private set; }
        public GameLaunchConfiguration? Configuration { get; private set; }

        public void Configure(GameLaunchConfiguration configuration)
        {
            Configuration = configuration;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return StopError is { } error
                ? Task.FromException(error)
                : StopCompletion.Task.WaitAsync(cancellationToken);
        }

        public Task OpenSteamAsync(CancellationToken cancellationToken)
        {
            SteamStarts++;
            return Task.CompletedTask;
        }

        public Task<IGameSession?> FindRunningAsync(string directory, CancellationToken cancellationToken)
        {
            Scans++;
            return Task.FromResult<IGameSession?>(null);
        }

        public Task<IGameSession> LaunchAsync(string directory, CancellationToken cancellationToken)
        {
            Starts++;
            return Task.FromResult<IGameSession>(Session);
        }

        public void Dispose()
        {
        }
    }

    private sealed class EmergencySession : IGameSession
    {
        public TaskCompletionSource Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public Task Completion => Exit.Task;

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
