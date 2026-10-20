using System.Text.Json.Serialization;
using DeadLocky.Features.UpdateApp.Model;

namespace DeadLocky.Features.UpdateApp.Api;

[JsonSerializable(typeof(GitHubRelease[]))]
[JsonSerializable(typeof(UpdateReceipt))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
