using System.Globalization;

namespace DeadLocky.Shared.Api.Diagnostics;

/// <summary>Immutable context flows across awaits without sharing mutable scope state.</summary>
internal static class LogContext
{
    private static readonly AsyncLocal<Frame?> Slot = new();
    internal sealed record Frame(Guid OperationId, Guid? ParentOperationId,
        IReadOnlyDictionary<string, string?> Fields);
    public static Frame? Current => Slot.Value;

    public static IDisposable Push(IReadOnlyDictionary<string, string?>? fields = null, bool operation = false)
    {
        Frame? previous = Current;
        var merged = new Dictionary<string, string?>();
        foreach ((string key, string? value) in (fields ?? new Dictionary<string, string?>())
                     .Concat(previous?.Fields ?? new Dictionary<string, string?>()).Take(48))
        {
            if (merged.Count < 24) { _ = merged.TryAdd(key, value); }
        }
        Slot.Value = new Frame(operation ? Guid.NewGuid() : previous?.OperationId ?? Guid.NewGuid(),
            operation ? previous?.OperationId : previous?.ParentOperationId, merged);
        return new Scope(previous);
    }

    public static IDisposable Restore(Frame? frame)
    {
        Frame? previous = Current;
        Slot.Value = frame;
        return new Scope(previous);
    }

    public static string? Format(object? value)
    {
        return value switch
        {
            null => null,
            string text => text,
            bool flag => flag ? "true" : "false",
            Guid id => id.ToString("D"),
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
            _ => "[unsupported value]"
        };
    }

    private sealed class Scope(Frame? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            Slot.Value = previous;
        }
    }
}
