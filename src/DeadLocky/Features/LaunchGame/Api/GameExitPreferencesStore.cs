using System.Text.Json;
using System.Text.Json.Serialization;
using DeadLocky.Shared.Api.Diagnostics;

namespace DeadLocky.Features.LaunchGame.Api;

internal sealed record GameExitPreferences(bool CloseSteamAfterExit = true);

[JsonSerializable(typeof(GameExitPreferences))]
internal partial class ExitPreferencesJsonContext : JsonSerializerContext;

internal sealed class GameExitPreferencesStore(string path)
{
    public GameExitPreferences Read()
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path),
                ExitPreferencesJsonContext.Default.GameExitPreferences) ?? new GameExitPreferences();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Event("game.exit-preferences", "using defaults", exception, level: Microsoft.Extensions.Logging.LogLevel.Information);
            return new GameExitPreferences();
        }
    }

    public void Save(GameExitPreferences preferences)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences,
                ExitPreferencesJsonContext.Default.GameExitPreferences));
            File.Move(temporary, path, true);
            AppLog.Event("game.exit-preferences", "saved", fields: AppLog.Data(("steam.closeAfterExit", preferences.CloseSteamAfterExit)));
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
