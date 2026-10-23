using System.Text.Json.Serialization;

namespace DeadLocky.Shared.Api.Diagnostics;

public sealed record SafeLogError(string Type, string Message, string Stack, SafeLogError[]? Inner, int HResult = 0,
    int? NativeErrorCode = null, int? HttpStatus = null, string? NetworkError = null);

internal sealed record LogEntry(DateTimeOffset Timestamp, string Severity, Guid SessionId, Guid OperationId,
    string Stage, double DurationMs, string Result, SafeLogError? Error, Guid? ParentOperationId,
    Dictionary<string, string?> Fields, int SchemaVersion = 2);

public sealed record LogSnapshot(string Name, byte[] Content, bool Truncated);

[JsonSerializable(typeof(LogEntry))]
internal sealed partial class LogJsonContext : JsonSerializerContext;
