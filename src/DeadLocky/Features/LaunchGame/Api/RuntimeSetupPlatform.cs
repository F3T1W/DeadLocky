using System.Diagnostics;
using System.Security.Cryptography;
using DeadLocky.Features.LaunchGame.Model;
using DeadLocky.Shared.Api.Diagnostics;

namespace DeadLocky.Features.LaunchGame.Api;

internal sealed record RuntimeDownload(string Name, string Url, string Sha256);

internal class RuntimeSetupPlatform(HttpClient? client = null)
{
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client = client ?? Client;

    public virtual Task StopPrefixProcessesAsync(GameLaunchConfiguration configuration, CancellationToken token)
    {
        return MacGameProcessScanner.StopPrefixProcessesAsync(configuration, token);
    }

    public virtual async Task CheckHostAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14))
        {
            throw new IOException("This environment requires macOS 14 or newer.");
        }

        try
        {
            _ = await RunAsync("/usr/bin/arch", ["-x86_64", "/usr/bin/true"], null, null, token);
        }
        catch (IOException exception)
        {
            throw new IOException("Rosetta is required. Use Install Rosetta in this wizard, then retry.", exception);
        }
    }

    public virtual Task DownloadAsync(RuntimeDownload download, string target,
        IProgress<RuntimeSetupProgress> progress, CancellationToken token)
    {
        return AppLog.RunAsync("runtime.download", () => DownloadCoreAsync(download, target, progress, token),
            AppLog.Data(("runtime.component", download.Name), ("download.destination", target)));
    }

    private async Task DownloadCoreAsync(RuntimeDownload download, string target,
        IProgress<RuntimeSetupProgress> progress, CancellationToken token)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target) && await MatchesAsync(target, download.Sha256, token))
        {
            AppLog.Event("runtime.download.cache", "checksum matched");
            return;
        }

        string partial = target + ".partial";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            using HttpResponseMessage response = await DiagnosticHttp.GetAsync(_client, download.Url,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            _ = response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using (Stream input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[81920];
                long received = 0;
                long lastReport = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    received += count;
                    if (received - lastReport < 1024 * 1024)
                    {
                        continue;
                    }

                    lastReport = received;
                    progress.Report(new RuntimeSetupProgress("Download",
                        $"Downloading {download.Name} · {received / 1048576} MB",
                        total is > 0 ? (double)received / total.Value : null));
                }
            }

            if (!await MatchesAsync(partial, download.Sha256, timeout.Token))
            {
                throw new IOException($"{download.Name} failed its integrity check. Retry the download.");
            }

            File.Move(partial, target, true);
            AppLog.Event("runtime.download.integrity", "verified", fields: AppLog.Data(("download.actualBytes", new FileInfo(target).Length)));
        }
        finally
        {
            File.Delete(partial);
        }
    }

    private static async Task<bool> MatchesAsync(string path, string sha256, CancellationToken token)
    {
        await using FileStream input = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(input, token);
        return Convert.ToHexString(hash).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    public virtual async Task<string> RunAsync(string executable, IEnumerable<string> arguments,
        string? prefix, string? libraryPath, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (prefix is not null)
        {
            start.Environment["WINEPREFIX"] = prefix;
            start.Environment["WINEDEBUG"] = "-all";
            start.Environment["WINEESYNC"] = "1";
            start.Environment["WINEDLLOVERRIDES"] = "mscoree,mshtml=;d3d11,d3d12,dxgi=n,b";
        }

        if (libraryPath is not null)
        {
            start.Environment["DYLD_FALLBACK_LIBRARY_PATH"] = libraryPath + ":/usr/lib";
        }

        using Process process = DiagnosticProcess.Start(start, "runtime.setup");
        process.StandardInput.Close();
        using var pipes = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<string> output = DiagnosticProcess.ReadBoundedAsync(process.StandardOutput, 1024 * 1024, pipes.Token);
        Task<string> error = DiagnosticProcess.ReadBoundedAsync(process.StandardError, 65536, pipes.Token);
        try
        {
            await process.WaitForExitAsync(token);
            string result = prefix is null ? await output : "";
            // Prefix commands can leave background Wine services holding the pipes open.
            string details = prefix is null ? await error : "";
            DiagnosticProcess.Stderr(details);
            return process.ExitCode == 0
                ? result
                : throw new IOException($"{Path.GetFileName(executable)} could not complete setup "
                                        + $"(exit {process.ExitCode}). " + (prefix is null
                                            ? details.Trim()[..Math.Min(400, details.Trim().Length)]
                                            : "Retry setup; check Rosetta and the selected Apple download."));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            await pipes.CancelAsync();
            try
            {
                _ = await Task.WhenAll(output, error);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
