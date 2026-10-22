using System.Text.RegularExpressions;

namespace DeadLocky.Shared.Api.Diagnostics;

public sealed partial class LogPrivacy(string home, string user)
{
    public string Redact(string? text, int limit = 8192)
    {
        string value = text ?? "";
        // Bound work before running expressions against third-party exception messages.
        value = value[..Math.Min(value.Length, 32768)];
        value = Secret().Replace(value, "$1=[redacted]");
        value = Url().Replace(value, match => Uri.TryCreate(match.Value, UriKind.Absolute, out Uri? uri)
            ? uri.Scheme + "://" + uri.Host + "/[redacted]" : "[url]");
        if (home.Length > 1)
        {
            value = value.Replace(home, "[home]", StringComparison.OrdinalIgnoreCase);
        }

        value = UserPath().Replace(value, "[user-path]");
        value = Email().Replace(value, "[email]");
        if (user.Length > 1)
        {
            value = Regex.Replace(value, @"\b" + Regex.Escape(user) + @"\b", "[user]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }

        return value.Length <= limit ? value : value[..limit] + " [truncated]";
    }

    public SafeLogError Error(Exception exception)
    {
        int nodes = 12;
        int remaining = 16384;
        return Visit(exception, 0);

        SafeLogError Visit(Exception item, int depth)
        {
            nodes--;
            // AggregateException.Message recursively expands all children before redaction; keep that work bounded too.
            string message = Redact(item is AggregateException group
                ? $"Multiple exceptions (count={group.InnerExceptions.Count})." : item.Message, Math.Min(2048, remaining));
            remaining = Math.Max(0, remaining - message.Length);
            string stack = Redact(item.StackTrace, Math.Min(4096, remaining));
            remaining = Math.Max(0, remaining - stack.Length);
            IEnumerable<Exception> inner = item is AggregateException aggregate
                ? aggregate.InnerExceptions : item.InnerException is { } cause ? [cause] : [];
            SafeLogError[] children = [.. inner.Take(4)
                .TakeWhile(_ => depth < 5 && nodes > 0 && remaining > 0)
                .Select(child => Visit(child, depth + 1))];

            return new SafeLogError(item.GetType().FullName ?? "Exception", message, stack, [.. children]);
        }
    }

    [GeneratedRegex(@"(?im)(authorization|proxy-authorization|cookie|set-cookie|password|passwd|pwd|access[_-]?token|refresh[_-]?token|token|api[_-]?key|secret|credentials)[\s""']*[:=][^\r\n]*|(?i)(bearer)\s+[^\s,;]+", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Secret();
    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex Url();
    [GeneratedRegex(@"/(?:Users|home)/[^\s<>""':]+", RegexOptions.CultureInvariant, 100)]
    private static partial Regex UserPath();
    [GeneratedRegex(@"[\w.!#$%&'*+/=?^`{|}~-]+@[\w.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Email();
}
