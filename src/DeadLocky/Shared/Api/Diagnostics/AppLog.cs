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
        Current.Record("session.start", "started", fields: Data(("process.id", Environment.ProcessId),
            ("schema.version", 2)));
    }

    public static Dictionary<string, string?> Data(params (string Key, object? Value)[] values)
    {
        var result = new Dictionary<string, string?>();
        foreach ((string key, object? value) in values.Take(24)) { result[key] = LogContext.Format(value); }
        return result;
    }

    public static void Event(string stage, string result, Exception? error = null,
        IReadOnlyDictionary<string, string?>? fields = null, LogLevel? level = null)
    {
        Current?.Record(stage, result, error, level: level ?? (error is null ? LogLevel.Information : LogLevel.Error), fields: fields);
    }

    public static T Run<T>(string stage, Func<T> action, IReadOnlyDictionary<string, string?>? fields = null)
    {
        using IDisposable scope = LogContext.Push(fields, true);
        var clock = Stopwatch.StartNew();
        Current?.Record(stage, "started");
        try
        {
            T value = action();
            Current?.Record(stage, "succeeded", durationMs: clock.Elapsed.TotalMilliseconds);
            return value;
        }
        catch (Exception exception)
        {
            Failed(stage, exception, clock);
            throw;
        }
    }

    public static async Task RunAsync(string stage, Func<Task> action, IReadOnlyDictionary<string, string?>? fields = null)
    {
        _ = await RunAsync(stage, async () => { await action(); return true; }, fields);
    }

    public static async Task<T> RunAsync<T>(string stage, Func<Task<T>> action,
        IReadOnlyDictionary<string, string?>? fields = null)
    {
        using IDisposable scope = LogContext.Push(fields, true);
        var clock = Stopwatch.StartNew();
        Current?.Record(stage, "started");
        try
        {
            T value = await action();
            Current?.Record(stage, "succeeded", durationMs: clock.Elapsed.TotalMilliseconds);
            return value;
        }
        catch (Exception exception)
        {
            Failed(stage, exception, clock);
            throw;
        }
    }

    private static void Failed(string stage, Exception exception, Stopwatch clock)
    {
        bool cancelled = exception is OperationCanceledException;
        Current?.Record(stage, cancelled ? "cancelled" : "failed", cancelled ? null : exception,
            durationMs: clock.Elapsed.TotalMilliseconds, level: cancelled ? LogLevel.Information : LogLevel.Error);
    }

    public static async Task FlushAsync(CancellationToken token)
    {
        if (Current is { } log) { await log.FlushAsync(token); }
    }

    public static async Task StopAsync()
    {
        if (Current is not { } log) { return; }
        Current = null;
        log.Record("session.end", "normal shutdown");
        await log.DisposeAsync();
    }
}
