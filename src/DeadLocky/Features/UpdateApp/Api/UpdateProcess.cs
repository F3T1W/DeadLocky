using System.Diagnostics;

namespace DeadLocky.Features.UpdateApp.Api;

internal static class UpdateProcess
{
    public static async Task<string> RunAsync(string executable, CancellationToken token, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new IOException($"Unable to start {executable}.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(token);
        Task<string> error = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
            string text = await output;
            string diagnostic = await error;
            return process.ExitCode == 0 ? text + diagnostic
                : throw new IOException($"{Path.GetFileName(executable)} failed: {diagnostic.Trim()}");
        }
        catch (OperationCanceledException)
        {
            if (process.HasExited)
            {
                throw;
            }

            process.Kill();
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }
}
