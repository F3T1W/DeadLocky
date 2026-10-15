using System.Text.Json.Serialization;
using DeadLocky.Entities.Mod.Model;
using DeadLocky.Features.ManageMods.Model;

namespace DeadLocky.Features.ManageMods.Api;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ModLibrary))]
[JsonSerializable(typeof(CatalogMod[]))]
[JsonSerializable(typeof(ModDownloads))]
[JsonSerializable(typeof(DeploymentJournal))]
[JsonSerializable(typeof(OwnedModFiles))]
internal sealed partial class ModJsonContext : JsonSerializerContext;

internal sealed record DeploymentJournal(
    string GameDirectory,
    string TransactionId,
    byte[] OriginalGameInfo,
    bool HadOwnedDirectory,
    bool Committed = false);

internal sealed record OwnedModFiles(Dictionary<string, string> Hashes);
