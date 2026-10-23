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
    private long _totalDropped;
    private LogEntry? _overflowError;

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
    public long DroppedEntries => Interlocked.Read(ref _totalDropped);

    public void Record(string stage, string result, Exception? error = null, Guid operation = default,
        double durationMs = 0, LogLevel level = LogLevel.Information,
        IReadOnlyDictionary<string, string?>? fields = null)
    {
        if (!IsEnabled(level)) { return; }
        try
        {
            var entry = new LogEntry(DateTimeOffset.UtcNow, level.ToString(), SessionId,
                operation == Guid.Empty ? LogContext.Current?.OperationId ?? Guid.NewGuid() : operation, Privacy.Redact(stage, 160),
                double.IsFinite(durationMs) ? Math.Max(0, durationMs) : 0, Privacy.Redact(result, 1024), error is null ? null : Privacy.Error(error),
                LogContext.Current?.ParentOperationId, Privacy.Fields(LogContext.Current?.Fields, fields));
            if (_queue.Writer.TryWrite(new Work(entry, null, null))) { return; }
            Drop();
            if (level >= LogLevel.Error) { _ = Interlocked.Exchange(ref _overflowError, entry); }
        }
        catch (Exception)
        {
            Drop();
        }
    }

    public async Task<LogSnapshot[]> SnapshotAsync(CancellationToken token)
    {
        var completion = new TaskCompletionSource<LogSnapshot[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(new Work(null, completion, null), token);
        return await completion.Task.WaitAsync(token);
    }

    public async Task FlushAsync(CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(new Work(null, null, completion), token);
        await completion.Task.WaitAsync(token);
    }

    private async Task ConsumeAsync()
    {
        try
        {
            DiagnosticFiles.RequireRegularPath(_directory);
            DiagnosticFiles.CreatePrivateDirectory(_directory);
            foreach (string marker in Directory.EnumerateFiles(_directory, "*.active").Take(50))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(marker), "N", out Guid priorSession))
                {
                    continue;
                }

                try
                {
                    DiagnosticFiles.RequireRegularPath(marker);
                    await using (new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        Record("session.previous", "unfinished; this is not proof of a crash", level: LogLevel.Warning,
                            fields: AppLog.Data(("session.previousId", priorSession)));
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
                        await WriteAsync(entry with { Stage = "logging.queue", Severity = "Warning", Result = $"dropped={lost}", Error = null });
                    }

                    await WriteAsync(entry);
                }

                if (!WriteFailed && Interlocked.Exchange(ref _overflowError, null) is { } overflow)
                {
                    await WriteAsync(overflow);
                }
                if (work.Snapshot is null && work.Flush is null) { continue; }
                long lostAtBarrier = Interlocked.Exchange(ref _dropped, 0);
                if (!WriteFailed && lostAtBarrier > 0)
                {
                    await WriteAsync(new LogEntry(DateTimeOffset.UtcNow, "Warning", SessionId, Guid.NewGuid(),
                        "logging.queue", 0, $"dropped={lostAtBarrier}", null, null, []));
                }
                if (_file is not null) { await _file.FlushAsync(); }
                if (work.Flush is { } flush)
                {
                    _ = WriteFailed ? flush.TrySetException(new IOException("Diagnostic logging is unavailable.")) : flush.TrySetResult();
                }
                if (work.Snapshot is { } snapshot) { _ = snapshot.TrySetResult(ReadSnapshot()); }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
            {
                WriteFailed = true;
                _ = work.Snapshot?.TrySetException(new IOException("Diagnostic logs are not readable.", exception));
                _ = work.Flush?.TrySetException(new IOException("Diagnostic logs could not be flushed.", exception));
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
            text = string.Join('\n', text.Split('\n').Select(SanitizeLine));
            result.Add(new LogSnapshot(file.Name, Encoding.UTF8.GetBytes(text), truncated));
        }

        return [.. result];
    }

    private void Drop()
    {
        _ = Interlocked.Increment(ref _dropped);
        _ = Interlocked.Increment(ref _totalDropped);
    }

    private string SanitizeLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) { return ""; }
        try
        {
            LogEntry? entry = JsonSerializer.Deserialize(line, LogJsonContext.Default.LogEntry);
            if (entry is null) { return ""; }
            int remainingNodes = 12;
            return JsonSerializer.Serialize(entry with
            {
                Severity = Privacy.Redact(entry.Severity, 16),
                Stage = Privacy.Redact(entry.Stage, 160),
                Result = Privacy.Redact(entry.Result, 1024),
                Error = Error(entry.Error),
                Fields = Privacy.Fields(null, entry.Fields)
            }, LogJsonContext.Default.LogEntry);

            SafeLogError? Error(SafeLogError? error, int depth = 0)
            {
                return error is null || depth > 5 || remainingNodes-- <= 0 ? null : error with
                {
                    Type = Privacy.Redact(error.Type, 160),
                    NetworkError = Privacy.Redact(error.NetworkError, 80),
                    Message = Privacy.Redact(error.Message, 2048),
                    Stack = Privacy.Redact(error.Stack, 4096),
                    Inner = [.. (error.Inner ?? []).Take(4).Select(child => Error(child, depth + 1)).OfType<SafeLogError>()]
                };
            }
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // Older/corrupted plain-text lines remain text; never run secret expressions over JSON syntax.
            return Privacy.Redact(line, 16384);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _ = _queue.Writer.TryComplete();
        await _writer;
    }

    public ILogger CreateLogger(string categoryName) { return new CategoryLogger(this, categoryName); }
    public bool IsEnabled(LogLevel logLevel) { return logLevel is >= LogLevel.Information and < LogLevel.None; }
    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        return LogContext.Push(state is IEnumerable<KeyValuePair<string, object?>> values
            ? AppLog.Data([.. values.Take(24).Select(static pair => (pair.Key, pair.Value))]) : null);
    }
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) { return; }
        try
        {
            Record("application", formatter(state, exception), exception, level: logLevel,
                fields: AppLog.Data(("event.id", eventId.Id), ("event.name", eventId.Name)));
        }
        catch (Exception) { Drop(); }
    }
    public void Dispose() { _ = _queue.Writer.TryComplete(); }

    private sealed class CategoryLogger(DiagnosticLog owner, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull { return owner.BeginScope(state); }
        public bool IsEnabled(LogLevel logLevel) { return owner.IsEnabled(logLevel); }
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            using IDisposable scope = LogContext.Push(AppLog.Data(("logger.category", category)));
            owner.Log(logLevel, eventId, state, exception, formatter);
        }
    }

    [GeneratedRegex(@"^log-[0-9a-f]{32}-[0-9]{4,10}\.jsonl$", RegexOptions.CultureInvariant)]
    private static partial Regex LogName();
    private sealed record Work(LogEntry? Entry, TaskCompletionSource<LogSnapshot[]>? Snapshot, TaskCompletionSource? Flush);
}
