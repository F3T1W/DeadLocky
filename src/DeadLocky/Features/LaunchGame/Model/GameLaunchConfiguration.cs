namespace DeadLocky.Features.LaunchGame.Model;

public sealed record GameLaunchConfiguration(string WineExecutable, string PrefixDirectory);

internal sealed record GameLaunchState(
    string Directory = "",
    bool InstallationReady = false,
    bool PreparationBusy = true,
    bool IsStarting = false,
    bool IsRunning = false,
    bool IsConfiguring = false,
    string? Error = null,
    bool EmergencyStopRequested = false,
    bool IsStopping = false)
{
    public bool CanLaunch => InstallationReady && !PreparationBusy && !IsStarting && !IsRunning && !IsConfiguring
                             && !EmergencyStopRequested;
}
