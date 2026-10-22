using System.Net.Mail;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DeadLocky.Features.ReportProblem.Model;

public sealed record ReportMetadata(string AppVersion, string Build, string Os, string Runtime,
    string Architecture, Guid SessionId, string? GameBuild = null);

public sealed partial record ProblemReport(Guid Id, string Message, string Email, ReportMetadata Metadata)
{
    public static void Validate(string message, string email)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 8000 || Encoding.UTF8.GetByteCount(message) > 32000)
        {
            throw new ArgumentException("Describe the problem using 1–8,000 characters (up to 32 KB UTF-8).");
        }

        if (email.Length > 254 || email.Any(char.IsControl) || (email.Length > 0
            && (!PlainEmail().IsMatch(email) || !MailAddress.TryCreate(email, out MailAddress? address) || address.Address != email
                || address.DisplayName.Length != 0 || !email.Contains('.'))))
        {
            throw new ArgumentException("Enter a plain email address, or leave the email field empty.");
        }
    }
    [GeneratedRegex(@"^[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainEmail();

}

internal sealed record ReportPayload(ProblemReport Report, string Archive);
internal sealed record ReportReceipt(bool Ok, Guid Id, string? Error);
internal sealed record ReportManifest(ProblemReport Report, string SnapshotPolicy, ReportLogFile[] Logs);
internal sealed record ReportLogFile(string Name, int Bytes, bool Truncated);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReportPayload))]
[JsonSerializable(typeof(ReportReceipt))]
[JsonSerializable(typeof(ReportManifest))]
internal sealed partial class ReportJsonContext : JsonSerializerContext;
