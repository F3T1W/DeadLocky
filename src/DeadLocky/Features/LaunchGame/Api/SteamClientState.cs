using DeadLocky.Entities.Steam.Model;
using DeadLocky.Shared.Api.Diagnostics;

namespace DeadLocky.Features.LaunchGame.Api;

internal static class SteamClientState
{
    public static SteamAccount? ReadSavedAccount(string steamDirectory)
    {
        var users = SteamVdf.LoadAsText(Path.Combine(steamDirectory, "config", "loginusers.vdf"));
        SteamVdf? recent = users?.Children
            .Where(static user => (user["MostRecent"].Value == "1" && user["AllowAutoLogin"].Value == "1")
                                  || (user["AutoLogin"].Value == "1" && user["RememberPassword"].Value == "1"))
            .OrderByDescending(static user => long.TryParse(user["Timestamp"].Value, out long time) ? time : 0)
            .FirstOrDefault();
        if (recent is null || !ulong.TryParse(recent.Name, out ulong id) || id == 0)
        {
            return null;
        }

        string? name = recent["PersonaName"].Value ?? recent["AccountName"].Value;
        return string.IsNullOrWhiteSpace(name) ? null : new SteamAccount(id, name);
    }

    public static bool IsInstalled(string steamDirectory, string directory, bool diagnose = false)
    {
        var state = SteamVdf.LoadAsText(ManifestPath(steamDirectory));
        bool appMatches = state?["appid"].Value == "1422450";
        bool stateReady = state?["StateFlags"].Value == "4";
        bool directoryMatches = state?["installdir"].Value == "Deadlock"
                                && MacGameProcessScanner.CanonicalPath(Path.Combine(steamDirectory,
                                    "steamapps", "common", "Deadlock")) == MacGameProcessScanner.CanonicalPath(directory);
        bool executableExists = File.Exists(Path.Combine(directory, "game", "bin", "win64", "deadlock.exe"));
        bool ready = appMatches && stateReady && directoryMatches && executableExists;
        if (diagnose)
        {
            AppLog.Event("steam.installation.state", ready ? "ready" : "not ready", fields: AppLog.Data(
                ("game.directory", directory), ("steam.manifestPresent", state is not null), ("steam.appMatches", appMatches),
                ("steam.stateFlags", state?["StateFlags"].Value), ("steam.directoryMatches", directoryMatches),
                ("game.executableExists", executableExists),
                ("game.buildId", ulong.TryParse(state?["buildid"].Value, out ulong build) ? build : null)));
        }
        return ready;
    }

    public static string ManifestPath(string steamDirectory)
    {
        return Path.Combine(steamDirectory, "steamapps", "appmanifest_1422450.acf");
    }
}
