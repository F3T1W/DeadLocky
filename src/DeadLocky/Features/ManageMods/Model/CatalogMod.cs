using System.Text.Json.Serialization;

namespace DeadLocky.Features.ManageMods.Model;

public sealed record CatalogMod
{
    public string RemoteId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public string Category { get; init; } = "";
    public string? Hero { get; init; }
    public string? RemoteUrl { get; init; }
    public string? Description { get; init; }
    public string[] Images { get; init; } = [];
    public string? ThumbnailUrl { get; init; }
    public string[] Tags { get; init; } = [];
    public ModDependency[]? Dependencies { get; init; } = [];
    public bool Downloadable { get; init; }

    [JsonPropertyName("isNSFW")] public bool IsNsfw { get; init; }

    public long DownloadCount { get; init; }
    public long Likes { get; init; }
    public DateTimeOffset RemoteUpdatedAt { get; init; }
}

public sealed record ModDependency(string Label, string? RemoteId, string? Level);

public sealed record ModDownload(string Url, long Size, string Name, string? Description, string? Md5Checksum);

public sealed record ModDownloads(ModDownload[] Downloads);

public sealed record PreparedMod(Guid Id, string Directory, string[] Roots);

public sealed record ModQuery(
    string Search = "",
    string Category = "",
    string Hero = "",
    int Sort = 0,
    bool IncludeNsfw = false)
{
    public const int PageSize = 24;

    public CatalogMod[] Filter(IEnumerable<CatalogMod> mods)
    {
        IEnumerable<CatalogMod> filtered = mods.Where(mod => (IncludeNsfw || !mod.IsNsfw)
                                                             && (Category.Length == 0 || mod.Category == Category) &&
                                                             (Hero.Length == 0 || mod.Hero == Hero)
                                                             && (Search.Length == 0 ||
                                                                 $"{mod.Name} {mod.Author} {mod.Hero} {mod.Category}"
                                                                     .Contains(Search.Trim(),
                                                                         StringComparison.OrdinalIgnoreCase)));
        return
        [
            .. Sort switch
            {
                1 => filtered.OrderByDescending(static mod => mod.DownloadCount),
                2 => filtered.OrderByDescending(static mod => mod.Likes),
                3 => filtered.OrderBy(static mod => mod.Name, StringComparer.OrdinalIgnoreCase),
                _ => filtered.OrderByDescending(static mod => mod.RemoteUpdatedAt)
            }
        ];
    }
}
