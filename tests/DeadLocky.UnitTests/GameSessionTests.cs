using System.Diagnostics;
using DeadLocky.Features.LaunchGame.Api;
using DeadLocky.Features.LaunchGame.Model;

namespace DeadLocky.UnitTests;

public sealed class GameSessionTests
{
    [Fact]
    public async Task PrefixCleanupLeavesOtherWinePrefixesRunning()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        string executable = Path.Combine(root, "wine-prefix-test");
        var compile = new ProcessStartInfo("/usr/bin/clang") { RedirectStandardInput = true };
        foreach (string argument in new[] { "-x", "c", "-o", executable, "-" })
        {
            compile.ArgumentList.Add(argument);
        }

        using (var compiler = Process.Start(compile)!)
        {
            await compiler.StandardInput.WriteAsync("#include <unistd.h>\nint main(void) { sleep(20); return 0; }\n");
            compiler.StandardInput.Close();
            await compiler.WaitForExitAsync();
            Assert.Equal(0, compiler.ExitCode);
        }

        using var own = Start(root);
        using var other = Start(root + "-other");
        try
        {
            Assert.False(own.HasExited);
            Assert.False(other.HasExited);
            string runtime = Path.Combine(root, "runtime");
            string wine = Path.Combine(runtime, "bin", "wine64");
            _ = Directory.CreateDirectory(Path.GetDirectoryName(wine)!);
            _ = Directory.CreateDirectory(Path.Combine(runtime, "lib", "D3DMetal.framework"));
            await File.WriteAllTextAsync(wine, "");
            string steam = Path.Combine(root, "drive_c", "Program Files", "Steam", "steam.exe");
            _ = Directory.CreateDirectory(Path.GetDirectoryName(steam)!);
            await File.WriteAllTextAsync(steam, "");
            using var launcher = new GptkGameLauncher(Path.Combine(root, "launch.json"));
            launcher.Configure(new GameLaunchConfiguration(wine, root));
            await launcher.StopAsync(CancellationToken.None);
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                launcher.OpenSteamAsync(CancellationToken.None));
            Assert.True(own.HasExited);
            Assert.False(other.HasExited);
        }
        finally
        {
            if (!own.HasExited)
            {
                own.Kill();
                await own.WaitForExitAsync();
            }

            if (!other.HasExited)
            {
                other.Kill();
                await other.WaitForExitAsync();
            }

            Directory.Delete(root, true);
        }

        return;

        Process Start(string prefix)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            start.ArgumentList.Add("20");
            start.Environment["WINEPREFIX"] = prefix;
            return Process.Start(start)!;
        }
    }

    [Fact]
    public async Task CompletionWaitsForSteamCleanupAfterConfirmedGameExit()
    {
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new GameProcessSession(static () => false, async token =>
        {
            cleanupStarted.SetResult();
            await cleanupFinished.Task.WaitAsync(token);
        });
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.Completion.IsCompleted);
        cleanupFinished.SetResult();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DetachingFromRunningGameDoesNotCloseSteam()
    {
        int cleanupCalls = 0;
        var session = new GameProcessSession(static () => true, delegate
        {
            _ = Interlocked.Increment(ref cleanupCalls);
            return Task.CompletedTask;
        });
        session.Dispose();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.Completion);
        Assert.Equal(0, cleanupCalls);
    }

    [Fact]
    public async Task TransientMissingProcessDoesNotTriggerCleanup()
    {
        int scans = 0;
        int cleanupCalls = 0;
        using var session = new GameProcessSession(() => Interlocked.Increment(ref scans) != 1, delegate
        {
            _ = Interlocked.Increment(ref cleanupCalls);
            return Task.CompletedTask;
        });
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.True(scans >= 2);
        Assert.Equal(0, cleanupCalls);
        Assert.False(session.Completion.IsCompleted);
    }
}
