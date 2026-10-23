using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeadLocky.Features.ReportProblem.Model;
using DeadLocky.Shared.Api.Diagnostics;

namespace DeadLocky.Features.ReportProblem.Api;

public sealed partial class ReportClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri? _endpoint;
    public bool IsConfigured => _endpoint is not null;

    public ReportClient(string? endpoint, HttpMessageHandler? handler = null)
    {
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || !SecureHost(uri, "script.google.com")
                || !EndpointPath().IsMatch(uri.AbsolutePath) || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            {
                throw new ArgumentException("Report delivery requires a Google Apps Script HTTPS /exec URL.");
            }

            _endpoint = uri;
        }

        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(45), MaxResponseContentBufferSize = 8192 };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DeadLocky-Reports/1.0");
    }

    public Task SendAsync(ProblemReport report, string archive, CancellationToken token)
    {
        // ZIP I/O and base64/JSON preparation must not run on AppKit's synchronization context.
        return AppLog.RunAsync("report.delivery", () => Task.Run(() => SendCoreAsync(report, archive, token), token),
            AppLog.Data(("report.id", report.Id)));
    }

    private async Task SendCoreAsync(ProblemReport report, string archive, CancellationToken token)
    {
        if (_endpoint is null) { throw new IOException("Report delivery is not configured. Export ZIP instead."); }
        ProblemReport.Validate(report.Message, report.Email);
        await using FileStream file = DiagnosticFiles.OpenRead(archive);
        if (file.Length is <= 0 or > ReportArchive.MaximumBytes) { throw new IOException("Invalid report ZIP size."); }
        byte[] bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, token);
        var payload = new ReportPayload(report, Convert.ToBase64String(bytes));
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, ReportJsonContext.Default.ReportPayload);
        if (json.Length > 12 * 1024 * 1024) { throw new IOException("The report request exceeds 12 MiB."); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Content = new ByteArrayContent(json);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        HttpResponseMessage response = await DiagnosticHttp.SendAsync(_http, request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        try
        {
            for (int count = 0; (int)response.StatusCode is >= 300 and < 400; count++)
            {
                Uri? next = response.Headers.Location;
                if (count >= 3 || response.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.SeeOther)
                    || next is not { IsAbsoluteUri: true } || !SecureHost(next, "script.googleusercontent.com")
                    || next.AbsolutePath != "/macros/echo" || next.Fragment.Length != 0
                    || !next.Query.Contains("user_content_key=", StringComparison.Ordinal))
                {
                    throw new IOException("Google returned an unexpected redirect. The report may have been accepted; retry with the same ID.");
                }

                response.Dispose();
                // ContentService serves a receipt at a one-time GET URL. Never resend the POST body.
                using var receiptRequest = new HttpRequestMessage(HttpMethod.Get, next);
                response = await DiagnosticHttp.SendAsync(_http, receiptRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }

            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json")
            {
                throw new IOException("No valid receipt from Google. Delivery is unconfirmed; retry with the same ID or export ZIP.");
            }

            await using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token);
            byte[] buffer = new byte[8193];
            int length = 0;
            while (length < buffer.Length)
            {
                int read = await source.ReadAsync(buffer.AsMemory(length), timeout.Token);
                if (read == 0) { break; }
                length += read;
            }

            if (length > 8192) { throw new IOException("The report receipt is too large."); }
            ReportReceipt? receipt = JsonSerializer.Deserialize(buffer.AsSpan(0, length), ReportJsonContext.Default.ReportReceipt);
            AppLog.Event("report.receipt", receipt is { Ok: true } && receipt.Id == report.Id ? "accepted" : "rejected",
                fields: AppLog.Data(("report.receiptMatches", receipt?.Id == report.Id), ("report.serviceCode", receipt?.Error)));
            if (receipt is null || !receipt.Ok || receipt.Id != report.Id)
            {
                string reason = receipt?.Error switch
                {
                    "quota" => "The report service has reached its sending limit.",
                    "uncertain" => "The email may already have been accepted. The service will not send a duplicate automatically.",
                    "disabled" => "The report service is disabled.",
                    _ => "The service did not confirm this report."
                };
                throw new IOException(reason + " Keep the same report ID when retrying, or export ZIP.");
            }
        }
        catch (JsonException exception)
        {
            throw new IOException("Google returned an invalid receipt. Delivery is unconfirmed.", exception);
        }
        finally { response.Dispose(); }
    }

    private static bool SecureHost(Uri uri, string host)
    {
        return uri is { Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } && uri.Host == host;
    }

    public void Dispose() { _http.Dispose(); }
    [GeneratedRegex("^/macros/s/[A-Za-z0-9_-]{20,256}/exec$", RegexOptions.CultureInvariant)]
    private static partial Regex EndpointPath();
}
