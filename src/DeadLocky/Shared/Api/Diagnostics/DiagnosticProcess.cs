using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace DeadLocky.Shared.Api.Diagnostics;

/// <summary>Preserves process arguments, environment and I/O mode. Only known technical stderr is recorded.</summary>
internal static partial class DiagnosticProcess
{
    public static Process Start(ProcessStartInfo start, string role)
    {
        return AppLog.Run("process.start", () =>
        {
            Process process = Process.Start(start) ?? throw new IOException("The child process could not be started.");
            var trace = new Trace(process, LogContext.Current);
            process.ErrorDataReceived += (_, args) => trace.Output(args.Data);
            process.Exited += (_, _) => trace.Exited();
            try { process.EnableRaisingEvents = true; }
            catch (Win32Exception exception) { AppLog.Event("process.observer", "unavailable", exception, level: LogLevel.Warning); }
            AppLog.Event("process.started", "started", fields: AppLog.Data(("process.id", process.Id)));
            return process;
        }, AppLog.Data(("process.role", role), ("process.executable", start.FileName),
            ("process.workingDirectory", start.WorkingDirectory), ("process.argumentCount", start.ArgumentList.Count),
            ("process.stdoutRedirected", start.RedirectStandardOutput), ("process.stderrRedirected", start.RedirectStandardError),
            ("wine.debug", start.Environment.TryGetValue("WINEDEBUG", out string? wineDebug) ? wineDebug : null),
            ("wine.esync", start.Environment.TryGetValue("WINEESYNC", out string? wineEsync) ? wineEsync : null),
            ("wine.prefix", start.Environment.TryGetValue("WINEPREFIX", out string? winePrefix) ? winePrefix : null),
            ("wine.dllOverrides", start.Environment.TryGetValue("WINEDLLOVERRIDES", out string? wineDllOverrides) ? wineDllOverrides : null)));
    }

    public static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var result = new StringBuilder(Math.Min(limit, 8192));
        char[] buffer = new char[4096];
        bool exceeded = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            int remaining = limit - result.Length;
            _ = result.Append(buffer, 0, Math.Min(read, remaining));
            exceeded |= read > remaining;
        }
        // Always drain the pipe before reporting overflow so a child cannot deadlock on a full pipe.
        return exceeded ? throw new IOException("Child process output exceeded the diagnostic capture limit.") : result.ToString();
    }

    public static void Stderr(string text)
    {
        foreach (string line in text.Split('\n').Take(8))
        {
            if (TechnicalError().IsMatch(line[..Math.Min(line.Length, 2048)])) { AppLog.Event("process.stderr", line, level: LogLevel.Warning); }
        }
    }

    private sealed class Trace(Process process, LogContext.Frame? context)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly int _pid = process.Id;
        private int _lines;
        private int _exited;
        public void Output(string? line)
        {
            using IDisposable scope = LogContext.Restore(context);
            if (line is null)
            {
                AppLog.Event("process.stderr-completed", "pipe drained", fields: AppLog.Data(("process.id", _pid),
                    ("process.suppressedErrors", Math.Max(0, Volatile.Read(ref _lines) - 8))));
                return;
            }
            if (!TechnicalError().IsMatch(line[..Math.Min(line.Length, 2048)]) || Interlocked.Increment(ref _lines) > 8) { return; }
            AppLog.Event("process.stderr", line, fields: AppLog.Data(("process.id", _pid)), level: LogLevel.Warning);
        }
        public void Exited()
        {
            if (Interlocked.Exchange(ref _exited, 1) != 0) { return; }
            using IDisposable scope = LogContext.Restore(context);
            try
            {
                AppLog.Event("process.exited", "observed", fields: AppLog.Data(("process.id", _pid),
                    ("process.exitCode", process.ExitCode), ("process.elapsedMs", _clock.Elapsed.TotalMilliseconds)),
                    level: process.ExitCode == 0 ? LogLevel.Information : LogLevel.Warning);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                AppLog.Event("process.exit-observation", "unavailable", exception, level: LogLevel.Warning);
            }
        }
    }

    [GeneratedRegex(@"(?i)^\s*(?:[0-9a-f]+:)?(?:err:|warn:|wine:|dyld\[|dyld:|fatal error|Unhandled exception|Library not loaded)|^(?:D3DMetal|DXGI|Metal).*?(?:error|fail)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex TechnicalError();
}
