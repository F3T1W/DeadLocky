using System.Text.Json.Serialization;

namespace DeadLocky.Features.UpdateApp.Model;

public sealed record AppRelease(AppVersion Version, string Tag, Uri Download, long Size, string Sha256);

internal sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string Tag,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("published_at")] DateTimeOffset Published,
    [property: JsonPropertyName("assets")] ReleaseAsset[]? Assets);

internal sealed record ReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string Download,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("digest")] string? Digest);

internal sealed record UpdateReceipt(string Target, string Transaction, string Version);
