using System.Text;
using System.Text.Json;
using DeadLocky.Shared.Api.Diagnostics;

namespace DeadLocky.UnitTests;

public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string _root = Path.Combine("/private/tmp", "DeadLockyLogs-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RotationRetentionAndStructuredEventsAreBounded()
    {
        await using DiagnosticLog log = Create(1024, 3);
        for (int index = 0; index < 100; index++) { log.Record("test.operation", new string('x', 600)); }
        LogSnapshot[] snapshot = await log.SnapshotAsync(CancellationToken.None);
        Assert.InRange(snapshot.Length, 1, 3);
        foreach (string file in Directory.GetFiles(_root, "*.jsonl"))
        {
            Assert.InRange(new FileInfo(file).Length, 1, 1024);
            foreach (string line in File.ReadLines(file))
            {
                using var json = JsonDocument.Parse(line);
                Assert.Equal(log.SessionId.ToString(), json.RootElement.GetProperty("SessionId").GetString());
                Assert.True(Guid.TryParse(json.RootElement.GetProperty("OperationId").GetString(), out _));
                Assert.True(json.RootElement.TryGetProperty("DurationMs", out _));
            }
        }
    }

    [Fact]
    public async Task RetentionRemovesOnlyOldAllowlistedLogs()
    {
        _ = Directory.CreateDirectory(_root);
        string old = Path.Combine(_root, $"log-{Guid.NewGuid():N}-0001.jsonl");
        await File.WriteAllTextAsync(old, "old");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));
        string personal = Path.Combine(_root, "settings.json");
        await File.WriteAllTextAsync(personal, "never collect");
        await using DiagnosticLog log = Create();
        _ = await log.SnapshotAsync(CancellationToken.None);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(personal));
    }

    [Fact]
    public async Task UnfinishedMarkerIsNotCalledAProvenCrashAndNormalShutdownRemovesOwnMarker()
    {
        _ = Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".active"), "");
        DiagnosticLog log = Create();
        log.Record("ready", "ready");
        _ = await log.SnapshotAsync(CancellationToken.None);
        await log.DisposeAsync();
        string text = string.Join("", await Task.WhenAll(Directory.GetFiles(_root, "*.jsonl")
            .Select(static file => File.ReadAllTextAsync(file, CancellationToken.None))));
        Assert.Contains("not proof of a crash", text);
        Assert.Empty(Directory.GetFiles(_root, "*.active"));
    }

    [Fact]
    public async Task WriteFailureAndThrowingFormatterDoNotEscapeToApplication()
    {
        await File.WriteAllTextAsync(_root, "not a directory");
        await using DiagnosticLog log = Create();
        log.Record("test", "entry", new BrokenMessageException());
        log.Log(Microsoft.Extensions.Logging.LogLevel.Error, default, "", null,
            static (_, _) => throw new InvalidOperationException("formatter failed"));
        _ = await log.SnapshotAsync(CancellationToken.None);
        Assert.True(log.WriteFailed);
        File.Delete(_root);
    }

    [Fact]
    public async Task SnapshotRejectsLinksAndDoesNotReadTheirTarget()
    {
        await using DiagnosticLog log = Create();
        _ = await log.SnapshotAsync(CancellationToken.None);
        string target = Path.Combine(_root, "personal.txt");
        await File.WriteAllTextAsync(target, "private contents");
        _ = File.CreateSymbolicLink(Path.Combine(_root, $"log-{Guid.NewGuid():N}-0000.jsonl"), target);
        _ = await Assert.ThrowsAsync<IOException>(() => log.SnapshotAsync(CancellationToken.None));
        Assert.Equal("private contents", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public void RedactionIncludesNestedExceptionsAndHasGlobalBounds()
    {
        var privacy = new LogPrivacy("/Users/test-person", "test-person");
        const string sensitive = "https://name:pass@example.com/path?token=secret /Users/test-person/private a@example.com\nAuthorization: Bearer hidden";
        string redacted = privacy.Redact(sensitive);
        foreach (string secret in new[] { "pass", "secret", "private", "a@example.com", "hidden", "test-person" })
        { Assert.DoesNotContain(secret, redacted); }
        Exception error = new IOException(sensitive);
        for (int depth = 0; depth < 8; depth++) { error = new AggregateException(Enumerable.Repeat(error, 4)); }
        SafeLogError safe = privacy.Error(error);
        Assert.InRange(Count(safe), 1, 12);
        Assert.DoesNotContain("hidden", JsonSerializer.Serialize(safe));
    }

    [Fact]
    public async Task LargeSnapshotUsesTailAndMarksTruncation()
    {
        await using DiagnosticLog log = Create();
        _ = await log.SnapshotAsync(CancellationToken.None);
        string path = Path.Combine(_root, $"log-{Guid.NewGuid():N}-0001.jsonl");
        await File.WriteAllTextAsync(path, new string('x', (1024 * 1024) + 100) + "\nlast line\n");
        LogSnapshot file = Assert.Single(await log.SnapshotAsync(CancellationToken.None));
        Assert.True(file.Truncated);
        Assert.Contains("last line", Encoding.UTF8.GetString(file.Content));
        Assert.True(file.Content.Length <= 2 * 1024 * 1024);
    }

    [Fact]
    public async Task ParallelOperationsKeepTheirParentsAndRestoreScopes()
    {
        await using DiagnosticLog log = Create();
        Guid parent;
        using (LogContext.Push(AppLog.Data(("profile.id", "parent")), true))
        {
            parent = LogContext.Current!.OperationId;
            List<Task> children = [];
            for (int index = 0; index < 4; index++) { children.Add(WriteChildAsync(log, index)); }
            await Task.WhenAll(children);
            log.Record("parent", "restored");
        }
        Assert.Null(LogContext.Current);
        LogEntry[] entries = await EntriesAsync(log);
        foreach (IGrouping<Guid, LogEntry> operation in entries.Where(static entry => entry.Stage.StartsWith("child", StringComparison.Ordinal))
                     .GroupBy(static entry => entry.OperationId))
        {
            Assert.Equal(2, operation.Count());
            Assert.All(operation, entry => Assert.Equal(parent, entry.ParentOperationId));
            _ = Assert.Single(operation.Select(static entry => entry.Fields["child"]).Distinct());
        }
        Assert.Equal(4, entries.Where(static entry => entry.Stage == "child.start").Select(static entry => entry.OperationId).Distinct().Count());
        Assert.Equal(parent, Assert.Single(entries, static entry => entry.Stage == "parent").OperationId);
        return;

        static async Task WriteChildAsync(DiagnosticLog sink, int index)
        {
            using IDisposable scope = LogContext.Push(AppLog.Data(("child", index)), true);
            sink.Record("child.start", "started");
            await Task.Yield();
            sink.Record("child.end", "succeeded");
        }
    }

    [Fact]
    public async Task SnapshotRedactsStructuredSecretsWithoutCorruptingJsonOrLosingContext()
    {
        await using DiagnosticLog log = Create();
        log.Record("test", "Authorization: Bearer SuperSecret", fields: AppLog.Data(
            ("accessToken", "OpaqueSecret"), ("replyTo", "reply@example.com"),
            ("file.path", "/Users/test-person/Games/Deadlock"),
            ("endpoint", "https://name:pass@example.com/private?token=hidden"),
            ("detail", "a quote \" and newline\npassword=HiddenPassword"), ("count", 42)));
        LogSnapshot[] snapshot = await log.SnapshotAsync(CancellationToken.None);
        string text = Encoding.UTF8.GetString(Assert.Single(snapshot).Content);
        foreach (string secret in new[] { "SuperSecret", "OpaqueSecret", "reply@example.com", "name:pass", "token=hidden", "HiddenPassword", "/Users/test-person" })
        { Assert.DoesNotContain(secret, text); }
        using var json = JsonDocument.Parse(text);
        Assert.Equal("42", json.RootElement.GetProperty("Fields").GetProperty("count").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("SchemaVersion").GetInt32());
    }

    [Fact]
    public async Task SnapshotResanitizesLegacyJsonFieldsAndExceptionStrings()
    {
        await using DiagnosticLog log = Create();
        _ = await log.SnapshotAsync(CancellationToken.None);
        string path = Path.Combine(_root, $"log-{Guid.NewGuid():N}-0000.jsonl");
        const string legacy = """{"Timestamp":"2026-10-23T00:00:00Z","Severity":"Error","SessionId":"00000000-0000-0000-0000-000000000001","OperationId":"00000000-0000-0000-0000-000000000002","Stage":"legacy","DurationMs":0,"Result":"password=legacy-secret","Error":null}""";
        await File.WriteAllTextAsync(path, legacy + "\n");
        LogEntry entry = Assert.Single(await EntriesAsync(log));
        Assert.Equal("legacy", entry.Stage);
        Assert.DoesNotContain("legacy-secret", entry.Result);
        Assert.Empty(entry.Fields);
    }

    [Fact]
    public async Task StandardLoggerPreservesCategoryEventAndStructuredScopes()
    {
        await using DiagnosticLog log = Create();
        Microsoft.Extensions.Logging.ILogger logger = log.CreateLogger("DeadLocky.Test");
        using (logger.BeginScope(new Dictionary<string, object?> { ["profile.id"] = 123, ["password"] = "ScopeSecret" }))
        {
            logger.Log(Microsoft.Extensions.Logging.LogLevel.Warning, new Microsoft.Extensions.Logging.EventId(17, "Blocked"),
                "operation", null, static (text, _) => text);
            logger.Log(Microsoft.Extensions.Logging.LogLevel.None, default, "never", null, static (text, _) => text);
        }
        LogEntry entry = Assert.Single(await EntriesAsync(log));
        Assert.Equal("DeadLocky.Test", entry.Fields["logger.category"]);
        Assert.Equal("17", entry.Fields["event.id"]);
        Assert.Equal("123", entry.Fields["profile.id"]);
        Assert.Equal("[redacted]", entry.Fields["password"]);
    }

    [Fact]
    public async Task NativeAndHttpErrorCodesSurviveWrapping()
    {
        await using DiagnosticLog log = Create();
        log.Record("native", "failed", new IOException("wrapped", new System.ComponentModel.Win32Exception(13)), level: Microsoft.Extensions.Logging.LogLevel.Error);
        log.Record("http", "failed", new HttpRequestException("denied", null, System.Net.HttpStatusCode.Forbidden));
        LogEntry[] entries = await EntriesAsync(log);
        Assert.Equal(13, Assert.Single(entries.Single(static entry => entry.Stage == "native").Error!.Inner!).NativeErrorCode);
        Assert.Equal(403, entries.Single(static entry => entry.Stage == "http").Error!.HttpStatus);
        Assert.NotEqual(0, entries[0].Error!.HResult);
    }

    [Fact]
    public async Task QueueOverflowIsCountedAndLastFailureSurvivesFlood()
    {
        await using DiagnosticLog log = Create();
        for (int index = 0; index < 10000; index++) { log.Record("flood", "entry"); }
        log.Record("important.failure", "failed", new IOException("last failure"), level: Microsoft.Extensions.Logging.LogLevel.Error);
        await log.FlushAsync(CancellationToken.None);
        LogEntry[] entries = await EntriesAsync(log);
        Assert.True(log.DroppedEntries > 0);
        Assert.Contains(entries, static entry => entry.Stage == "logging.queue");
        Assert.Contains(entries, static entry => entry is { Stage: "important.failure", Error: not null });
    }

    [Fact]
    public async Task CancelledFlushAndInvalidDurationsCannotBreakWriter()
    {
        await using DiagnosticLog log = Create();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => log.FlushAsync(cancellation.Token));
        log.Record("duration", "value", durationMs: double.NaN);
        await log.FlushAsync(CancellationToken.None);
        Assert.Equal(0, Assert.Single(await EntriesAsync(log)).DurationMs);
        Assert.False(log.WriteFailed);
    }

    [Fact]
    public async Task FieldAndExceptionBudgetsKeepEventsBounded()
    {
        await using DiagnosticLog log = Create();
        var fields = new Dictionary<string, string?>();
        for (int index = 0; index < 100; index++) { fields.Add("field" + index, new string('x', 5000)); }
        log.Record("bounds", "value", new IOException(new string('e', 50000)), fields: fields);
        LogEntry entry = Assert.Single(await EntriesAsync(log));
        Assert.InRange(entry.Fields.Count, 1, 24);
        Assert.True(entry.Fields.Sum(static pair => pair.Key.Length + pair.Value!.Length) < 4600);
        Assert.InRange(entry.Error!.Message.Length, 1, 2100);
    }

    private static async Task<LogEntry[]> EntriesAsync(DiagnosticLog log)
    {
        LogSnapshot[] snapshot = await log.SnapshotAsync(CancellationToken.None);
        return [.. snapshot.SelectMany(static file => Encoding.UTF8.GetString(file.Content).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Select(static line => JsonSerializer.Deserialize(line, LogJsonContext.Default.LogEntry)!)];
    }

    private sealed class BrokenMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException("Broken exception formatter.");
    }

    private DiagnosticLog Create(int limit = 2 * 1024 * 1024, int retained = 6)
    { return new DiagnosticLog(_root, new LogPrivacy("/Users/test-person", "test-person"), limit, retained); }
    private static int Count(SafeLogError error) { return 1 + (error.Inner?.Sum(Count) ?? 0); }
    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
}
