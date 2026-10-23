namespace DeadLocky.Shared.Api.Diagnostics;

/// <summary>Logs transport metadata only; request/response bodies and headers are never dumped.</summary>
internal static class DiagnosticHttp
{
    public static async Task<HttpResponseMessage> GetAsync(HttpClient client, string url,
        HttpCompletionOption completion, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync(client, request, completion, token);
    }

    public static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request,
        HttpCompletionOption completion, CancellationToken token)
    {
        return AppLog.RunAsync("http.request", async () =>
        {
            try
            {
                HttpResponseMessage response = await client.SendAsync(request, completion, token);
                AppLog.Event("http.response", response.IsSuccessStatusCode ? "received" : "non-success status",
                    fields: AppLog.Data(("http.status", (int)response.StatusCode),
                        ("http.contentLength", response.Content.Headers.ContentLength),
                        ("http.finalHost", response.RequestMessage?.RequestUri?.Host),
                        ("http.retryAfterSeconds", response.Headers.RetryAfter?.Delta?.TotalSeconds)),
                    level: response.IsSuccessStatusCode ? Microsoft.Extensions.Logging.LogLevel.Information
                        : Microsoft.Extensions.Logging.LogLevel.Warning);
                return response;
            }
            catch (OperationCanceledException)
            {
                AppLog.Event("http.interrupted", token.IsCancellationRequested ? "caller cancelled" : "transport timeout",
                    level: token.IsCancellationRequested ? Microsoft.Extensions.Logging.LogLevel.Information
                        : Microsoft.Extensions.Logging.LogLevel.Warning);
                throw;
            }
        }, AppLog.Data(("http.method", request.Method.Method), ("http.host", request.RequestUri?.Host)));
    }
}
