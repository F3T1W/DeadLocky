using System.Diagnostics;
using DeadLocky.Shared.Api.Diagnostics;

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

        using Process process = DiagnosticProcess.Start(start, "update.tool");
        Task<string> output = DiagnosticProcess.ReadBoundedAsync(process.StandardOutput, 1024 * 1024, token);
        Task<string> error = DiagnosticProcess.ReadBoundedAsync(process.StandardError, 65536, token);
        try
        {
            await process.WaitForExitAsync(token);
            string text = await output;
            string diagnostic = await error;
            DiagnosticProcess.Stderr(diagnostic);
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
