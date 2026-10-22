using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using DeadLocky.Features.ReportProblem.Api;
using DeadLocky.Features.ReportProblem.Model;
using DeadLocky.Shared.Api.Diagnostics;

namespace DeadLocky.UnitTests;

public sealed class ProblemReportTests : IDisposable
{
    private const string Endpoint = "https://script.google.com/macros/s/ABCDEFGHIJKLMNOPQRST123456/exec";
    private readonly string _root = Path.Combine("/private/tmp", "DeadLockyReports-" + Guid.NewGuid().ToString("N"));
    private static ProblemReport Draft => new(Guid.NewGuid(), "Description deliberately included", "reply@example.com",
        new ReportMetadata("0.2.0", "2", "macOS", ".NET 10", "Arm64", Guid.NewGuid()));

    [Theory]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("message", "not-email")]
    [InlineData("message", "name <a@example.com>")]
    [InlineData("message", "a@example.com\r\nBcc: other@example.com")]
    public void InvalidFieldsAreRejected(string message, string email)
    { _ = Assert.Throws<ArgumentException>(() => ProblemReport.Validate(message, email)); }

    [Fact]
    public void FieldLengthsAndOptionalEmailAreChecked()
    {
        ProblemReport.Validate("message", "");
        ProblemReport.Validate("message", "a+tag@example.com");
        _ = Assert.Throws<ArgumentException>(static () => ProblemReport.Validate(new string('a', 8001), ""));
        _ = Assert.Throws<ArgumentException>(static () => ProblemReport.Validate("message", new string('a', 255)));
    }

    [Fact]
    public async Task ZipContainsOnlyAllowlistedRedactedLogsAndExplicitFormData()
    {
        string logs = Path.Combine(_root, "Logs");
        await using var log = new DiagnosticLog(logs, new LogPrivacy("/Users/person", "person"));
        log.Record("operation", "/Users/person/private\npassword=secret");
        _ = await log.SnapshotAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(logs, "settings.json"), "private configuration", CancellationToken.None);
        var archive = new ReportArchive(log, Path.Combine(_root, "Temp"));
        ProblemReport report = Draft;
        string path = await archive.CreateAsync(report, CancellationToken.None);
        await using (ZipArchive zip = await ZipFile.OpenReadAsync(path, CancellationToken.None))
        {
            Assert.Equal(2, zip.Entries.Count);
            Assert.Contains(zip.Entries, static entry => entry.FullName == "report.json");
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                using var reader = new StreamReader(await entry.OpenAsync(CancellationToken.None));
                string text = await reader.ReadToEndAsync(CancellationToken.None);
                Assert.DoesNotContain("private configuration", text);
                Assert.DoesNotContain("secret", text);
                Assert.DoesNotContain("/Users/person", text);
                if (entry.FullName == "report.json")
                {
                    using var json = JsonDocument.Parse(text);
                    Assert.Equal(report.Message, json.RootElement.GetProperty("report").GetProperty("message").GetString());
                    Assert.Equal(report.Email, json.RootElement.GetProperty("report").GetProperty("email").GetString());
                }
                else { Assert.StartsWith("Logs/log-", entry.FullName); }
            }
        }
        Assert.InRange(new FileInfo(path).Length, 1, ReportArchive.MaximumBytes);
        ReportArchive.Delete(path);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "Temp")));
    }

    [Fact]
    public async Task CancellationAndOversizedSnapshotLeaveNoPartialArchives()
    {
        await using var log = new DiagnosticLog(Path.Combine(_root, "Logs"), new LogPrivacy("", "aa"));
        _ = await log.SnapshotAsync(CancellationToken.None);
        string temp = Path.Combine(_root, "Temp");
        var archive = new ReportArchive(log, temp);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archive.CreateAsync(Draft, cancellation.Token));
        Assert.False(Directory.Exists(temp));
        // Redaction expansion must not bypass the per-file bound; its partial ZIP is removed.
        string path = Path.Combine(_root, "Logs", $"log-{Guid.NewGuid():N}-0000.jsonl");
        await File.WriteAllTextAsync(path, string.Concat(Enumerable.Repeat("aa\n", 340000)), CancellationToken.None);
        _ = await Assert.ThrowsAsync<IOException>(() => archive.CreateAsync(Draft, CancellationToken.None));
        Assert.Empty(Directory.GetFileSystemEntries(temp));
    }

    [Fact]
    public async Task ExportRejectsSymbolicTemporaryRoot()
    {
        await using var log = new DiagnosticLog(Path.Combine(_root, "Logs"), new LogPrivacy("", ""));
        _ = await log.SnapshotAsync(CancellationToken.None);
        string target = Path.Combine(_root, "Private");
        _ = Directory.CreateDirectory(target);
        _ = Directory.CreateSymbolicLink(Path.Combine(_root, "Link"), target);
        var archive = new ReportArchive(log, Path.Combine(_root, "Link"));
        _ = await Assert.ThrowsAsync<IOException>(() => archive.CreateAsync(Draft, CancellationToken.None));
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Theory]
    [InlineData("http://script.google.com/macros/s/ABCDEFGHIJKLMNOPQRST/exec")]
    [InlineData("https://evil.com/macros/s/ABCDEFGHIJKLMNOPQRST/exec")]
    [InlineData("https://script.google.com/macros/s/ABCDEFGHIJKLMNOPQRST/dev")]
    [InlineData("https://script.google.com/macros/s/ABCDEFGHIJKLMNOPQRST/exec?token=x")]
    public void EndpointMustBeExpectedPublicDeployment(string endpoint)
    { _ = Assert.Throws<ArgumentException>(() => new ReportClient(endpoint)); }

    [Fact]
    public async Task TrustedReceiptRedirectUsesGetAndNeverForwardsPayload()
    {
        ProblemReport report = Draft;
        int calls = 0;
        using var client = new ReportClient(Endpoint, new Handler(async (request, token) =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                string body = await request.Content!.ReadAsStringAsync(token);
                Assert.Contains(report.Id.ToString(), body);
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri("https://script.googleusercontent.com/macros/echo?user_content_key=one-time");
                return response;
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content);
            Assert.Null(request.Headers.Authorization);
            return Json($$"""{"ok":true,"id":"{{report.Id}}"}""");
        }));
        await client.SendAsync(report, CreateZip(), CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("wrong-id")]
    [InlineData("false")]
    [InlineData("html")]
    [InlineData("server-error")]
    [InlineData("oversized")]
    [InlineData("invalid-json")]
    [InlineData("evil-redirect")]
    [InlineData("307")]
    public async Task MissingOrInvalidReceiptNeverReportsSuccess(string scenario)
    {
        using var client = new ReportClient(Endpoint, new Handler((_, _) =>
        {
            HttpResponseMessage response = scenario switch
            {
                "wrong-id" => Json($$"""{"ok":true,"id":"{{Guid.NewGuid()}}"}"""),
                "false" => Json("{\"ok\":false,\"error\":\"quota\"}"),
                "html" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Login</html>") },
                "server-error" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                "oversized" => Json(new string('x', 8193)),
                "invalid-json" => Json("not-json"),
                _ => new HttpResponseMessage(scenario == "307" ? HttpStatusCode.TemporaryRedirect : HttpStatusCode.Found)
                { Headers = { Location = new Uri("https://evil.com/macros/echo?user_content_key=x") } }
            };
            return Task.FromResult(response);
        }));
        _ = await Assert.ThrowsAsync<IOException>(() => client.SendAsync(Draft, CreateZip(), CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfflineAndTimeoutAreFailures(bool timeout)
    {
        using var client = new ReportClient(Endpoint, new Handler((_, _) => timeout
            ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout"))
            : Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"))));
        Exception? exception = await Record.ExceptionAsync(() => client.SendAsync(Draft, CreateZip(), CancellationToken.None));
        Assert.True(timeout ? exception is OperationCanceledException : exception is HttpRequestException);
    }

    [Fact]
    public async Task CancellationAndRetryKeepTheReportId()
    {
        ProblemReport report = Draft;
        int calls = 0;
        List<Guid> ids = [];
        using var client = new ReportClient(Endpoint, new Handler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            ids.Add(json.RootElement.GetProperty("report").GetProperty("id").GetGuid());
            return ++calls == 1 ? throw new HttpRequestException("lost receipt") : Json($$"""{"ok":true,"id":"{{report.Id}}"}""");
        }));
        string zip = CreateZip();
        _ = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(report, zip, CancellationToken.None));
        await client.SendAsync(report, zip, CancellationToken.None);
        Assert.Equal(new[] { report.Id, report.Id }, ids);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(report, zip, cancellation.Token));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task MissingEndpointLeavesExportAvailableAndSendDisabled()
    {
        using var client = new ReportClient(null);
        Assert.False(client.IsConfigured);
        _ = await Assert.ThrowsAsync<IOException>(() => client.SendAsync(Draft, "unused", CancellationToken.None));
    }

    [Fact]
    public async Task CancellationInterruptsAnInFlightRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new ReportClient(Endpoint, new Handler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new IOException("Unreachable without cancellation.");
        }));
        using var cancellation = new CancellationTokenSource();
        Task sending = client.SendAsync(Draft, CreateZip(), cancellation.Token);
        await started.Task;
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
    }

    [Fact]
    public async Task ExportOverwriteIsAtomicAndCancellationPreservesExistingFile()
    {
        string zip = CreateZip();
        string destination = Path.Combine(_root, "export.zip");
        await File.WriteAllTextAsync(destination, "original", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReportArchive.SaveAsync(zip, destination, cancellation.Token));
        Assert.Equal("original", await File.ReadAllTextAsync(destination, CancellationToken.None));
        await ReportArchive.SaveAsync(zip, destination, CancellationToken.None);
        Assert.Equal(await File.ReadAllBytesAsync(zip, CancellationToken.None), await File.ReadAllBytesAsync(destination, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_root, "*.partial"));
    }

    private string CreateZip()
    {
        _ = Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("report.json").Open());
        writer.Write("{}");
        return path;
    }
    private static HttpResponseMessage Json(string text)
    { return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") }; }
    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { return send(request, cancellationToken); }
    }
}
