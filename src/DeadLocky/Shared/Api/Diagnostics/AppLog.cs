using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DeadLocky.Shared.Api.Diagnostics;

public static class AppLog
{
    public static DiagnosticLog? Current { get; private set; }

    public static void Start(string directory)
    {
        Current = new DiagnosticLog(directory, new LogPrivacy(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName));
        Current.Record("session.start", "started");
    }

    public static void Event(string stage, string result, Exception? error = null)
    {
        Current?.Record(stage, result, error, level: error is null ? LogLevel.Information : LogLevel.Error);
    }

    public static async Task RunAsync(string stage, Func<Task> action)
    {
        _ = await RunAsync(stage, async () => { await action(); return true; });
    }

    public static async Task<T> RunAsync<T>(string stage, Func<Task<T>> action)
    {
        var id = Guid.NewGuid();
        var clock = Stopwatch.StartNew();
        Current?.Record(stage, "started", operation: id);
        try
        {
            T value = await action();
            Current?.Record(stage, "succeeded", operation: id, durationMs: clock.Elapsed.TotalMilliseconds);
            return value;
        }
        catch (OperationCanceledException)
        {
            Current?.Record(stage, "cancelled", operation: id, durationMs: clock.Elapsed.TotalMilliseconds);
            throw;
        }
        catch (Exception exception)
        {
            Current?.Record(stage, "failed", exception, id, clock.Elapsed.TotalMilliseconds, LogLevel.Error);
            throw;
        }
    }

    public static async Task StopAsync()
    {
        if (Current is { } log)
        {
            log.Record("session.end", "normal shutdown");
            await log.DisposeAsync();
        }
    }
}
