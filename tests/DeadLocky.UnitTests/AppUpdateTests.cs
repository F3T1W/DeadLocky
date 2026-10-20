using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeadLocky.Features.UpdateApp.Api;
using DeadLocky.Features.UpdateApp.Model;

namespace DeadLocky.UnitTests;

public sealed class AppUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DeadLockyUpdates-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("v0.2.0", "0.1.9", 1)]
    [InlineData("0.2.0-beta.2", "0.2.0-beta.10", -1)]
    [InlineData("0.2.0", "0.2.0-rc.1", 1)]
    [InlineData("0.2.0+build.7", "v0.2.0", 0)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)]
    [InlineData("1.0.0-beta", "1.0.0-alpha", 1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-beta.99999999999999999999", "1.0.0-beta.9999999999999999999", 1)]
    public void VersionsFollowSemanticPrecedence(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(AppVersion.Parse(left)!.CompareTo(AppVersion.Parse(right))));
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("-1.2.3")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-beta.01")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("1.2.3/../other")]
    [InlineData("1.2.3.4")]
    public void InvalidVersionsAreRejected(string version)
    {
        Assert.Null(AppVersion.Parse(version));
    }

    [Fact]
    public async Task PublicPreviewIsFoundWithoutLatestEndpoint()
    {
        HttpRequestMessage? sent = null;
        using HttpClient http = Client(request =>
        {
            sent = request;
            return Json(Release("v0.2.0"));
        });
        AppRelease? update = await new AppUpdateClient(http).FindUpdateAsync(AppVersion.Parse("0.1.0")!,
            CancellationToken.None);
        Assert.NotNull(sent);
        Assert.Equal("api.github.com", sent.RequestUri!.Host);
        Assert.Contains("/releases?", sent.RequestUri.AbsoluteUri);
        Assert.NotEmpty(sent.Headers.UserAgent);
        Assert.Equal("0.2.0", update!.Version.ToString());
        Assert.Equal("v0.2.0", update.Tag);
    }

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("0.3.0")]
    public async Task SameOrOlderReleaseNeverDowngrades(string installed)
    {
        using HttpClient http = Client(static _ => Json(Release("v0.2.0")));
        Assert.Null(await new AppUpdateClient(http).FindUpdateAsync(AppVersion.Parse(installed)!, CancellationToken.None));
    }

    [Fact]
    public async Task DraftAndUnparseableTagsAreIgnored()
    {
        string drafts = Release("v0.3.0", draft: true)[1..^1] + "," + Release("nightly")[1..^1];
        using HttpClient http = Client(_ => Json("[" + drafts + "]"));
        Assert.Null(await new AppUpdateClient(http).FindUpdateAsync(AppVersion.Parse("0.1.0")!, CancellationToken.None));
    }

    [Fact]
    public async Task CanonicalChecksumAssetIsSupportedWhenApiHasNoDigest()
    {
        using HttpClient http = Client(static request => request.RequestUri!.Host == "api.github.com"
            ? Json(Release("v0.2.0", digest: null))
            : Text(new string('a', 64) + "  DeadLocky-0.2.0-apple-silicon.dmg\n"));
        AppRelease? update = await new AppUpdateClient(http).FindUpdateAsync(AppVersion.Parse("0.1.0")!,
            CancellationToken.None);
        Assert.Equal(new string('A', 64), update!.Sha256);
    }

    [Theory]
    [InlineData("https://evil.example/update.dmg")]
    [InlineData("https://github.com/OtherOwner/DeadLocky/releases/download/v0.2.0/DeadLocky-0.2.0-apple-silicon.dmg")]
    [InlineData("http://github.com/F3T1W/DeadLocky/releases/download/v0.2.0/DeadLocky-0.2.0-apple-silicon.dmg")]
    [InlineData("https://github.com/F3T1W/DeadLocky/releases/download/v0.2.0/DeadLocky-0.2.0-apple-silicon.dmg?token=bad")]
    public async Task ForeignOrUnsafeDownloadLinksAreRejected(string url)
    {
        using HttpClient http = Client(_ => Json(Release("v0.2.0", url: url)));
        _ = await Assert.ThrowsAsync<IOException>(() => new AppUpdateClient(http)
            .FindUpdateAsync(AppVersion.Parse("0.1.0")!, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidChecksumAndMissingAssetsFailClosed()
    {
        using HttpClient badHash = Client(_ => Json(Release("v0.2.0", digest: "sha256:bad")));
        _ = await Assert.ThrowsAsync<IOException>(() => new AppUpdateClient(badHash)
            .FindUpdateAsync(AppVersion.Parse("0.1.0")!, CancellationToken.None));
        using HttpClient missing = Client(_ => Json("[{\"tag_name\":\"v0.2.0\",\"draft\":false,\"assets\":[]}]"));
        _ = await Assert.ThrowsAsync<IOException>(() => new AppUpdateClient(missing)
            .FindUpdateAsync(AppVersion.Parse("0.1.0")!, CancellationToken.None));
    }

    [Fact]
    public async Task SuccessfulDownloadMatchesSizeAndHash()
    {
        _ = Directory.CreateDirectory(_root);
        byte[] bytes = [.. "a verified disk image fixture"u8];
        using HttpClient http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        string path = Path.Combine(_root, "Update.dmg");
        await new AppUpdateClient(http).DownloadAsync(Package(bytes), path, new Progress<double>(), CancellationToken.None);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task CorruptTruncatedAndOversizedDownloadsAreRejected()
    {
        _ = Directory.CreateDirectory(_root);
        byte[] original = [1, 2, 3];
        foreach (byte[] received in new byte[][] { [1, 2, 4], [1, 2], [1, 2, 3, 4] })
        {
            using HttpClient http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(received)
            });
            _ = await Assert.ThrowsAsync<IOException>(() => new AppUpdateClient(http).DownloadAsync(Package(original),
                Path.Combine(_root, Guid.NewGuid().ToString("N")), new Progress<double>(), CancellationToken.None));
        }
    }

    [Fact]
    public async Task CancelledDownloadDoesNotOverwriteExistingFiles()
    {
        _ = Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "Update.dmg");
        await File.WriteAllTextAsync(path, "keep");
        using HttpClient http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AppUpdateClient(http)
            .DownloadAsync(Package([1]), path, new Progress<double>(), cancelled.Token));
        Assert.Equal("keep", await File.ReadAllTextAsync(path, CancellationToken.None));
    }

    private static AppRelease Package(byte[] bytes)
    {
        return new AppRelease(AppVersion.Parse("0.2.0")!, "v0.2.0",
            new Uri("https://github.com/F3T1W/DeadLocky/releases/download/v0.2.0/DeadLocky-0.2.0-apple-silicon.dmg"),
            bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static string Release(string tag, bool draft = false, string? digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        string? url = null)
    {
        const string filename = "DeadLocky-0.2.0-apple-silicon.dmg";
        string download = url ?? $"https://github.com/F3T1W/DeadLocky/releases/download/{tag}/{filename}";
        return JsonSerializer.Serialize(new[]
        {
            new
            {
                tag_name = tag, draft, published_at = "2026-10-20T12:00:00Z",
                assets = new[]
                {
                    new { name = filename, browser_download_url = download, size = 10, digest },
                    new { name = filename + ".sha256", browser_download_url = download + ".sha256", size = 110, digest = (string?)null }
                }
            }
        });
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> response)
    {
        return new HttpClient(new Handler(response));
    }

    private static HttpResponseMessage Json(string value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }

    private static HttpResponseMessage Text(string value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(value) };
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(request));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
