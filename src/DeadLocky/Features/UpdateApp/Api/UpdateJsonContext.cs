using System.Text.Json.Serialization;
using DeadLocky.Features.UpdateApp.Model;

namespace DeadLocky.Features.UpdateApp.Api;

[JsonSerializable(typeof(GitHubRelease[]))]
[JsonSerializable(typeof(UpdateReceipt))]
[JsonSerializable(typeof(UpdateOutcome))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;

internal sealed record UpdateOutcome(string Stage, int Code, string Transaction, string Timestamp);
