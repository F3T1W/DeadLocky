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

    private sealed class BrokenMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException("Broken exception formatter.");
    }

    private DiagnosticLog Create(int limit = 2 * 1024 * 1024, int retained = 6)
    { return new DiagnosticLog(_root, new LogPrivacy("/Users/test-person", "test-person"), limit, retained); }
    private static int Count(SafeLogError error) { return 1 + error.Inner.Sum(Count); }
    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
}
