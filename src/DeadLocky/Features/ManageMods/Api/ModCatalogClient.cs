using System.Net;
using System.Text.Json;
using DeadLocky.Features.ManageMods.Model;
using DeadLocky.Shared.Api.Diagnostics;

namespace DeadLocky.Features.ManageMods.Api;

public sealed class ModCatalogClient(HttpClient http, string cachePath) : IDisposable
{
    private const string Api = "https://api.deadlockmods.app/api/v2/";

    public void Dispose()
    {
        http.Dispose();
    }

    public CatalogMod[] ReadCached()
    {
        try
        {
            return File.Exists(cachePath)
                ? JsonSerializer.Deserialize(File.ReadAllBytes(cachePath), ModJsonContext.Default.CatalogModArray) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            AppLog.Event("catalog.cache.read", "failed", exception);
            return [];
        }
    }

    public Task<CatalogMod[]> RefreshAsync(CancellationToken token)
    {
        return AppLog.RunAsync("catalog.refresh", () => RefreshCoreAsync(token));
    }

    private async Task<CatalogMod[]> RefreshCoreAsync(CancellationToken token)
    {
        byte[] bytes = await GetBytesAsync(Api + "mods", 64 * 1024 * 1024, token);
        CatalogMod[] mods = JsonSerializer.Deserialize(bytes, ModJsonContext.Default.CatalogModArray)
                            ?? throw new IOException("The catalog returned an empty response.");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        ModFiles.AtomicWrite(cachePath, bytes);
        AppLog.Event("catalog.refresh.result", $"items={mods.Length}");
        return mods;
    }

    public Task<ModDownload[]> DownloadsAsync(string remoteId, CancellationToken token)
    {
        return AppLog.RunAsync("catalog.versions", () => DownloadsCoreAsync(remoteId, token), AppLog.Data(("mod.remoteId", remoteId)));
    }

    private async Task<ModDownload[]> DownloadsCoreAsync(string remoteId, CancellationToken token)
    {
        byte[] bytes = await GetBytesAsync(Api + "mods/" + Uri.EscapeDataString(remoteId) + "/downloads",
            2 * 1024 * 1024, token);
        return JsonSerializer.Deserialize(bytes, ModJsonContext.Default.ModDownloads)?.Downloads ?? [];
    }

    public Task DownloadAsync(ModDownload download, string destination, IProgress<long>? progress,
        CancellationToken token)
    {
        return AppLog.RunAsync("mod.download", () => DownloadCoreAsync(download, destination, progress, token),
            AppLog.Data(("download.expectedBytes", download.Size), ("download.destination", destination)));
    }

    private async Task DownloadCoreAsync(ModDownload download, string destination, IProgress<long>? progress,
        CancellationToken token)
    {
        RequireHttps(download.Url);
        using HttpResponseMessage response = await DiagnosticHttp.GetAsync(http, download.Url,
            HttpCompletionOption.ResponseHeadersRead, token);
        _ = response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > ModFiles.MaximumDownload)
        {
            throw new IOException("The download exceeds the 1 GiB limit.");
        }

        await using Stream input = await response.Content.ReadAsStreamAsync(token);
        await using FileStream output = File.Create(destination);
        long length = await ModFiles.CopyBoundedAsync(input, output, ModFiles.MaximumDownload, token, progress);
        AppLog.Event("mod.download.bytes", "received", fields: AppLog.Data(("download.actualBytes", length)));
        if (download.Size > 0 && length != download.Size)
        {
            throw new IOException("The downloaded file is incomplete or its size has changed. Refresh the catalog.");
        }
    }

    public Task<byte[]> ImageAsync(string url, CancellationToken token)
    {
        return GetBytesAsync(url, 8 * 1024 * 1024, token);
    }

    private Task<byte[]> GetBytesAsync(string url, int limit, CancellationToken token)
    {
        return AppLog.RunAsync("network.catalog", () => GetBytesCoreAsync(url, limit, token));
    }

    private async Task<byte[]> GetBytesCoreAsync(string url, int limit, CancellationToken token)
    {
        RequireHttps(url);
        using HttpResponseMessage response = await DiagnosticHttp.GetAsync(http, url, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new IOException("The catalog is rate limiting requests. Please try again shortly.");
        }

        _ = response.EnsureSuccessStatusCode();
        await using Stream input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        long received = await ModFiles.CopyBoundedAsync(input, output, limit, token);
        AppLog.Event("catalog.response.bytes", "received", fields: AppLog.Data(("download.actualBytes", received), ("download.limit", limit)));
        return output.ToArray();
    }

    private static void RequireHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new IOException("The catalog supplied an invalid HTTPS URL.");
        }
    }
}
