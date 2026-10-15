namespace DeadLocky.Features.LaunchGame.Api;

public interface IEmergencyGameStopper
{
    Task StopAsync(CancellationToken cancellationToken);
}
