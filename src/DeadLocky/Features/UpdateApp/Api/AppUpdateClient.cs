using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using DeadLocky.Features.UpdateApp.Model;

namespace DeadLocky.Features.UpdateApp.Api;

public sealed class AppUpdateClient(HttpClient http)
{
    private const long MaximumDownload = 512L * 1024 * 1024;
    private const string Repository = "F3T1W/DeadLocky";

    public async Task<AppRelease?> FindUpdateAsync(AppVersion installed, CancellationToken token)
    {
        // GitHub's /latest endpoint excludes previews; the first release is a preview too.
        List<GitHubRelease> releases = [];
        for (int page = 1; page <= 10; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{Repository}/releases?per_page=100&page={page}");
            request.Headers.UserAgent.ParseAdd("DeadLocky-Updater/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using HttpResponseMessage response = await http.SendAsync(request, token);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                throw new IOException("GitHub's update check limit was reached. Try again later.");
            }

            _ = response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(token);
            GitHubRelease[] batch = await JsonSerializer.DeserializeAsync(stream,
                UpdateJsonContext.Default.GitHubReleaseArray, token) ?? [];
            releases.AddRange(batch);
            if (batch.Length < 100)
            {
                break;
            }
        }

        GitHubRelease? latest = releases.Where(static release => !release.Draft)
            .Where(release => AppVersion.Parse(release.Tag) is { } version && version.CompareTo(installed) > 0)
            .OrderByDescending(static release => release.Published).FirstOrDefault();
        if (latest is null)
        {
            return null;
        }

        if (AppVersion.Parse(latest.Tag) is not { } next)
        {
            throw new IOException("The release has no valid version.");
        }

        string filename = $"DeadLocky-{next}-apple-silicon.dmg";
        ReleaseAsset asset = latest.Assets?.SingleOrDefault(item => item.Name == filename)
                             ?? throw new IOException($"Release {latest.Tag} has no Apple Silicon DMG yet.");
        Uri download = AssetUri(asset.Download, latest.Tag, filename);
        if (asset.Size is <= 0 or > MaximumDownload)
        {
            throw new IOException("The update's download size is invalid.");
        }

        string digest = asset.Digest ?? await ReadChecksumAsync(latest, filename, token);
        return !digest.StartsWith("sha256:", StringComparison.Ordinal) || !IsHash(digest[7..])
            ? throw new IOException("The update has no valid SHA-256 checksum.")
            : new AppRelease(next, latest.Tag, download, asset.Size, digest[7..].ToUpperInvariant());
    }

    private async Task<string> ReadChecksumAsync(GitHubRelease release, string filename, CancellationToken token)
    {
        ReleaseAsset checksum = release.Assets!.SingleOrDefault(item => item.Name == filename + ".sha256")
                                ?? throw new IOException("The release has no SHA-256 checksum.");
        if (checksum.Size is <= 0 or > 4096)
        {
            throw new IOException("Invalid update checksum file.");
        }

        Uri checksumUri = AssetUri(checksum.Download, release.Tag, checksum.Name);
        string content = await http.GetStringAsync(checksumUri, token);
        string[] parts = content.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[1] == filename ? "sha256:" + parts[0] : "";
    }

    private static Uri AssetUri(string value, string tag, string filename)
    {
        return !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || (uri.AbsolutePath != $"/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{filename}"
                && uri.AbsolutePath != $"/F3T1W/MilkDeadLockDry/releases/download/{Uri.EscapeDataString(tag)}/{filename}")
            ? throw new IOException("The release points outside the DeadLocky GitHub repository.")
            : uri;
    }

    private static bool IsHash(string value)
    {
        return value.Length == 64 && value.All(char.IsAsciiHexDigit);
    }

    public async Task DownloadAsync(AppRelease release, string path, IProgress<double> progress,
        CancellationToken token)
    {
        // Validate again because callers can construct an AppRelease directly.
        _ = AssetUri(release.Download.AbsoluteUri, release.Tag,
            $"DeadLocky-{release.Version}-apple-silicon.dmg");
        if (release.Size is <= 0 or > MaximumDownload || !IsHash(release.Sha256))
        {
            throw new IOException("Invalid update download metadata.");
        }

        using HttpResponseMessage response = await http.GetAsync(release.Download,
            HttpCompletionOption.ResponseHeadersRead, token);
        _ = response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != release.Size)
        {
            throw new IOException("The update's download size changed. Check for updates again.");
        }

        await using Stream source = await response.Content.ReadAsStreamAsync(token);
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long total = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, token)) > 0)
            {
                total += count;
                if (total > release.Size)
                {
                    throw new IOException("The update is larger than declared by GitHub.");
                }

                hash.AppendData(buffer, 0, count);
                await destination.WriteAsync(buffer.AsMemory(0, count), token);
                progress.Report((double)total / release.Size);
            }

            if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset())
                    .Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The update failed its SHA-256 integrity check. It was not installed.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
