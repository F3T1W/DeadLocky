using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using DeadLocky.Features.LaunchGame.Api;
using DeadLocky.Features.ReportProblem.Api;
using DeadLocky.Features.ReportProblem.Model;
using DeadLocky.Features.UpdateApp.Api;
using DeadLocky.Features.UpdateApp.Model;
using DeadLocky.Shared.Api.Diagnostics;

namespace DeadLocky.UnitTests;

[CollectionDefinition("Application logging", DisableParallelization = true)]
public sealed class ApplicationLoggingFixture;

[Collection("Application logging")]
public sealed class ApplicationLoggingTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine("/private/tmp", "DeadLocky-Logging-" + Guid.NewGuid().ToString("N"));
    private static DiagnosticLog Log => AppLog.Current!;

    public Task InitializeAsync()
    {
        AppLog.Start(Path.Combine(_root, "Logs"));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await AppLog.StopAsync();
        Directory.Delete(_root, true);
    }

    [Fact]
    public async Task NestedFailuresKeepTheOperationChainAndDuration()
    {
        _ = await Assert.ThrowsAsync<IOException>(static () => AppLog.RunAsync("outer", static () =>
            AppLog.RunAsync("inner", static () => Task.FromException(new IOException("failure"))), AppLog.Data(("profile.id", 123))));
        LogEntry[] entries = await EntriesAsync();
        LogEntry outer = Assert.Single(entries, static entry => entry is { Stage: "outer", Result: "failed" });
        LogEntry inner = Assert.Single(entries, static entry => entry is { Stage: "inner", Result: "failed" });
        Assert.Equal(outer.OperationId, inner.ParentOperationId);
        Assert.Equal("123", inner.Fields["profile.id"]);
        Assert.True(inner.DurationMs > 0);
        Assert.Equal("System.IO.IOException", inner.Error!.Type);
        Assert.Null(LogContext.Current);
    }

    [Fact]
    public async Task NonSuccessHttpRecordsStatusHostAndRetryWithoutRequestSecrets()
    {
        using var client = new HttpClient(new ResponseHandler(static (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return Task.FromResult(response);
        }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/private?token=HttpSecret");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "HeaderSecret");
        using HttpResponseMessage result = await DiagnosticHttp.SendAsync(client, request,
            HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        Assert.Equal(HttpStatusCode.TooManyRequests, result.StatusCode);
        LogEntry[] entries = await EntriesAsync();
        LogEntry response = Assert.Single(entries, static entry => entry.Stage == "http.response");
        Assert.Equal("429", response.Fields["http.status"]);
        Assert.Equal("60", response.Fields["http.retryAfterSeconds"]);
        Assert.Equal("example.invalid", response.Fields["http.host"]);
        string text = string.Join('\n', (await Log.SnapshotAsync(CancellationToken.None)).Select(static file => Encoding.UTF8.GetString(file.Content)));
        Assert.DoesNotContain("HttpSecret", text);
        Assert.DoesNotContain("HeaderSecret", text);
        Assert.DoesNotContain("/private?", text);
    }

    [Theory]
    [InlineData(true, "caller cancelled")]
    [InlineData(false, "transport timeout")]
    public async Task TransportTimeoutIsDistinguishedFromUserCancellation(bool cancel, string expected)
    {
        using var token = new CancellationTokenSource();
        if (cancel) { await token.CancelAsync(); }
        using var client = new HttpClient(new ResponseHandler(static (_, _) => throw new TaskCanceledException()));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/");
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiagnosticHttp.SendAsync(client, request,
            HttpCompletionOption.ResponseHeadersRead, token.Token));
        Assert.Equal(expected, Assert.Single(await EntriesAsync(), static entry => entry.Stage == "http.interrupted").Result);
    }

    [Fact]
    public async Task RealChildRecordsExitAndBoundedTechnicalStderrWithoutDumpingArgumentsOrEnvironment()
    {
        var start = new ProcessStartInfo("/bin/sh")
        { RedirectStandardError = true, UseShellExecute = false, Environment = { ["PRIVATE_TEST_TOKEN"] = "EnvironmentSecret" } };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("echo 'password=UnrelatedSecret' >&2; i=0; while [ $i -lt 30 ]; do echo 'err:module:load Library not loaded; password=ProcessSecret' >&2; i=$((i+1)); done; exit 37");
        using Process process = DiagnosticProcess.Start(start, "test.child");
        process.BeginErrorReadLine();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(37, process.ExitCode);
        LogEntry[] entries = await EntriesAsync();
        LogEntry exited = Assert.Single(entries, static entry => entry.Stage == "process.exited");
        Assert.Equal("37", exited.Fields["process.exitCode"]);
        Assert.Equal(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), exited.Fields["process.id"]);
        Assert.Equal(8, entries.Count(static entry => entry.Stage == "process.stderr"));
        Assert.Equal("22", Assert.Single(entries, static entry => entry.Stage == "process.stderr-completed").Fields["process.suppressedErrors"]);
        string text = string.Join('\n', (await Log.SnapshotAsync(CancellationToken.None)).Select(static file => Encoding.UTF8.GetString(file.Content)));
        foreach (string secret in new[] { "EnvironmentSecret", "UnrelatedSecret", "ProcessSecret", "PRIVATE_TEST_TOKEN" })
        { Assert.DoesNotContain(secret, text); }
    }

    [Fact]
    public async Task OversizedProcessCaptureDrainsBeforeFailing()
    {
        await using var input = new MemoryStream(new byte[65536]);
        using var reader = new StreamReader(input);
        _ = await Assert.ThrowsAsync<IOException>(() => DiagnosticProcess.ReadBoundedAsync(reader, 1024, CancellationToken.None));
        Assert.Equal(input.Length, input.Position);
    }

    [Fact]
    public async Task SteamFolderConflictRecordsBothCanonicalPathsAndLinkState()
    {
        string steam = Path.Combine(_root, "Steam");
        string registered = Path.Combine(steam, "steamapps", "common", "Deadlock");
        string selected = Path.Combine(_root, "Selected");
        _ = Directory.CreateDirectory(registered);
        _ = Directory.CreateDirectory(selected);
        _ = Assert.Throws<IOException>(() => SteamInstallationLink.Register(Path.Combine(steam, "steam.exe"), selected));
        LogEntry paths = Assert.Single(await EntriesAsync(), static entry => entry.Stage == "steam.installation.paths");
        Assert.Equal("mismatch", paths.Result);
        Assert.Equal(registered, paths.Fields["game.registeredCanonical"]);
        Assert.Equal(selected, paths.Fields["game.selectedCanonical"]);
        Assert.Equal("true", paths.Fields["steam.linkExists"]);
        Assert.True(Directory.Exists(registered));
    }

    [Fact]
    public async Task FailedLogStorageIsDeclaredInExportedReport()
    {
        await AppLog.StopAsync();
        string blocked = Path.Combine(_root, "blocked");
        await File.WriteAllTextAsync(blocked, "not a folder");
        AppLog.Start(blocked);
        var report = new ProblemReport(Guid.NewGuid(), "test", "", new ReportMetadata("0.3.0", "3", "macOS", ".NET", "Arm64", Log.SessionId));
        var archive = new ReportArchive(Log, Path.Combine(_root, "Reports"));
        string path = await archive.CreateAsync(report, CancellationToken.None);
        await using System.IO.Compression.ZipArchive zip = await System.IO.Compression.ZipFile.OpenReadAsync(path, CancellationToken.None);
        using var reader = new StreamReader(await zip.GetEntry("report.json")!.OpenAsync(CancellationToken.None));
        using var json = JsonDocument.Parse(await reader.ReadToEndAsync(CancellationToken.None));
        Assert.True(json.RootElement.GetProperty("logging").GetProperty("writeFailed").GetBoolean());
        Assert.Equal(0, json.RootElement.GetProperty("logging").GetProperty("droppedEntries").GetInt64());
        _ = await Assert.ThrowsAsync<IOException>(static () => Log.FlushAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FailedUpdateHelperPreservesAppAndReportsOutcomeOnNextLaunch()
    {
        if (!OperatingSystem.IsMacOS()) { return; }
        string state = Path.Combine(_root, "Updates");
        _ = Directory.CreateDirectory(state);
        string target = Path.Combine(_root, "Original.app");
        _ = Directory.CreateDirectory(target);
        string sentinel = Path.Combine(target, "sentinel");
        await File.WriteAllTextAsync(sentinel, "original application");
        string transaction = Path.Combine(_root, ".DeadLockyUpdate-" + Guid.NewGuid().ToString("N"));
        string contents = Path.Combine(transaction, "Next.app", "Contents");
        _ = Directory.CreateDirectory(contents);
        await File.WriteAllTextAsync(Path.Combine(contents, "Info.plist"), "test preflight failure");
        string pending = Path.Combine(state, "pending.json");
        await File.WriteAllTextAsync(pending, "{}");
        string script = Path.Combine(transaction, "InstallUpdate.sh");
        await using (Stream resource = typeof(AppUpdateClient).Assembly.GetManifestResourceStream("DeadLocky.InstallUpdate.sh")!)
        await using (FileStream file = File.Create(script)) { await resource.CopyToAsync(file); }
        var start = new ProcessStartInfo("/bin/bash") { UseShellExecute = false };
        foreach (string argument in new[] { script, "2147483647", target, transaction, pending })
        { start.ArgumentList.Add(argument); }
        using Process process = DiagnosticProcess.Start(start, "test.update-helper");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(0, process.ExitCode);
        Assert.Equal("original application", await File.ReadAllTextAsync(sentinel));
        Assert.False(File.Exists(pending));
        await new MacUpdateInstaller(target, state).ConfirmLaunchAsync(AppVersion.Parse("0.3.0")!);
        LogEntry outcome = Assert.Single(await EntriesAsync(), static entry => entry.Stage == "update.previous-helper");
        Assert.Equal("failed", outcome.Result);
        Assert.NotEqual("0", outcome.Fields["update.exitCode"]);
    }

    private static async Task<LogEntry[]> EntriesAsync()
    {
        LogSnapshot[] snapshot = await Log.SnapshotAsync(CancellationToken.None);
        return [.. snapshot.SelectMany(static file => Encoding.UTF8.GetString(file.Content).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Select(static line => JsonSerializer.Deserialize(line, LogJsonContext.Default.LogEntry)!)];
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { return response(request, cancellationToken); }
    }
}
