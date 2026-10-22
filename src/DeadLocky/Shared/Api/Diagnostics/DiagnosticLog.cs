using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace DeadLocky.Shared.Api.Diagnostics;

/// <summary>A bounded ILogger file sink. One reader owns rotation, writes and export snapshots.</summary>
public sealed partial class DiagnosticLog : ILoggerProvider, ILogger, IAsyncDisposable
{
    private readonly Channel<Work> _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(512)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly string _directory;
    private readonly int _fileLimit;
    private readonly int _retained;
    private readonly Task _writer;
    private FileStream? _marker;
    private FileStream? _file;
    private int _sequence;
    private long _dropped;
    private bool _stopped;

    public DiagnosticLog(string directory, LogPrivacy privacy, int fileLimit = 2 * 1024 * 1024, int retained = 6)
    {
        _directory = Path.GetFullPath(directory);
        Privacy = privacy;
        _fileLimit = Math.Clamp(fileLimit, 1024, 2 * 1024 * 1024);
        _retained = Math.Clamp(retained, 2, 6);
        _writer = Task.Run(ConsumeAsync);
    }

    public Guid SessionId { get; } = Guid.NewGuid();
    private LogPrivacy Privacy { get; }
    public bool WriteFailed { get; private set; }

    public void Record(string stage, string result, Exception? error = null, Guid operation = default,
        double durationMs = 0, LogLevel level = LogLevel.Information)
    {
        try
        {
            var entry = new LogEntry(DateTimeOffset.UtcNow, level.ToString(), SessionId,
                operation == Guid.Empty ? Guid.NewGuid() : operation, Privacy.Redact(stage, 160),
                durationMs, Privacy.Redact(result, 1024), error is null ? null : Privacy.Error(error));
            if (!_queue.Writer.TryWrite(new Work(entry, null)))
            {
                _ = Interlocked.Increment(ref _dropped);
            }
        }
        catch (Exception)
        {
            _ = Interlocked.Increment(ref _dropped);
        }
    }

    public async Task<LogSnapshot[]> SnapshotAsync(CancellationToken token)
    {
        var completion = new TaskCompletionSource<LogSnapshot[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(new Work(null, completion), token);
        return await completion.Task.WaitAsync(token);
    }

    private async Task ConsumeAsync()
    {
        try
        {
            DiagnosticFiles.RequireRegularPath(_directory);
            DiagnosticFiles.CreatePrivateDirectory(_directory);
            foreach (string marker in Directory.EnumerateFiles(_directory, "*.active").Take(50))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(marker), "N", out _))
                {
                    continue;
                }

                try
                {
                    DiagnosticFiles.RequireRegularPath(marker);
                    await using (new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        Record("session.previous", "unfinished; this is not proof of a crash", level: LogLevel.Warning);
                    }

                    File.Delete(marker);
                }
                catch (IOException) { }
            }

            _marker = new FileStream(Path.Combine(_directory, SessionId.ToString("N") + ".active"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            Prune();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WriteFailed = true;
        }

        await foreach (Work work in _queue.Reader.ReadAllAsync())
        {
            try
            {
                if (work.Entry is { } entry && !WriteFailed)
                {
                    long lost = Interlocked.Exchange(ref _dropped, 0);
                    if (lost > 0)
                    {
                        await WriteAsync(entry with { Stage = "logging.queue", Result = $"dropped={lost}", Error = null });
                    }

                    await WriteAsync(entry);
                }

                if (work.Snapshot is not { } snapshot) { continue; }
                if (_file is not null) { await _file.FlushAsync(); }
                _ = snapshot.TrySetResult(ReadSnapshot());
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
            {
                WriteFailed = true;
                _ = work.Snapshot?.TrySetException(new IOException("Diagnostic logs are not readable.", exception));
            }
        }

        try
        {
            if (_file is not null) { await _file.DisposeAsync(); }
        }
        catch (IOException) { WriteFailed = true; }

        if (_marker is null) { return; }
        try { await _marker.DisposeAsync(); }
        catch (IOException) { WriteFailed = true; }
        try { File.Delete(Path.Combine(_directory, SessionId.ToString("N") + ".active")); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task WriteAsync(LogEntry entry)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entry, LogJsonContext.Default.LogEntry);
        if (_file is null || _file.Length + bytes.Length + 1 > _fileLimit)
        {
            if (_file is not null)
            {
                await _file.DisposeAsync();
            }

            string path = Path.Combine(_directory, $"log-{SessionId:N}-{_sequence++:D4}.jsonl");
            _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
            Prune();
        }

        await _file.WriteAsync(bytes);
        await _file.WriteAsync("\n"u8.ToArray());
        await _file.FlushAsync();
    }

    private IEnumerable<FileInfo> Logs()
    {
        return new DirectoryInfo(_directory).EnumerateFiles("log-*.jsonl")
            .Where(static file => LogName().IsMatch(file.Name))
            .OrderByDescending(static file => file.LastWriteTimeUtc).ThenByDescending(static file => file.Name);
    }

    private void Prune()
    {
        foreach (FileInfo file in Logs().Skip(_retained - (_file is null ? 1 : 0)).ToArray())
        {
            DiagnosticFiles.RequireRegularPath(file.FullName);
            file.Delete();
        }

        foreach (FileInfo file in Logs().Where(static file => file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7)).ToArray())
        {
            DiagnosticFiles.RequireRegularPath(file.FullName);
            file.Delete();
        }
    }

    private LogSnapshot[] ReadSnapshot()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        List<LogSnapshot> result = [];
        foreach (FileInfo file in Logs().Take(5))
        {
            using FileStream stream = DiagnosticFiles.OpenRead(file.FullName);
            bool truncated = stream.Length > 1024 * 1024;
            if (truncated)
            {
                _ = stream.Seek(-1024 * 1024, SeekOrigin.End);
            }

            byte[] bytes = new byte[(int)Math.Min(stream.Length, 1024 * 1024)];
            stream.ReadExactly(bytes);
            string text = Encoding.UTF8.GetString(bytes);
            if (truncated)
            {
                int line = text.IndexOf('\n');
                text = line < 0 ? "" : text[(line + 1)..];
            }

            // Redact line by line so truncation limits never drop the rest of a log file.
            text = string.Join('\n', text.Split('\n').Select(line => Privacy.Redact(line, 16384)));
            result.Add(new LogSnapshot(file.Name, Encoding.UTF8.GetBytes(text), truncated));
        }

        return [.. result];
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopped) { return; }
        _stopped = true;
        _ = _queue.Writer.TryComplete();
        await _writer;
    }

    public ILogger CreateLogger(string categoryName) { return this; }
    public bool IsEnabled(LogLevel logLevel) { return logLevel >= LogLevel.Information; }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull { return null; }
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        try
        {
            if (IsEnabled(logLevel)) { Record("application", formatter(state, exception), exception, level: logLevel); }
        }
        catch (Exception) { _ = Interlocked.Increment(ref _dropped); }
    }
    public void Dispose() { _ = _queue.Writer.TryComplete(); }

    [GeneratedRegex(@"^log-[0-9a-f]{32}-[0-9]{4,10}\.jsonl$", RegexOptions.CultureInvariant)]
    private static partial Regex LogName();
    private sealed record Work(LogEntry? Entry, TaskCompletionSource<LogSnapshot[]>? Snapshot);
}
