using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace DeadLocky.Features.UpdateApp.Api;

[SupportedOSPlatform("macos")]
public sealed class PreparedAppUpdate(string transaction, string pending, string target, FileStream lease)
    : IDisposable
{
    private bool _handedOff;

    public async Task StartInstallerAsync(int processId, CancellationToken token)
    {
        if (_handedOff)
        {
            throw new InvalidOperationException("The update installer has already started.");
        }

        string original = (await File.ReadAllTextAsync(Path.Combine(transaction, "original.sha256"), token)).Trim();
        if (await MacUpdateInstaller.FingerprintAsync(target, token) != original)
        {
            throw new IOException("The installed app changed while downloading. Check for updates again.");
        }

        _ = await UpdateProcess.RunAsync("/usr/bin/codesign", token, "--verify", "--deep", "--strict", target);
        // Give the shell the same two-file fingerprint in a format that shasum can verify.
        string[] files = [Path.Combine(target, "Contents/Info.plist"), Path.Combine(target, "Contents/MacOS/DeadLocky")];
        List<string> checksums = [];
        foreach (string file in files)
        {
            await using FileStream stream = File.OpenRead(file);
            checksums.Add(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)) + "  " + file);
        }

        await File.WriteAllLinesAsync(Path.Combine(transaction, "original-files.sha256"), checksums, token);
        var start = new ProcessStartInfo("/bin/bash") { UseShellExecute = false };
        foreach (string argument in new[]
                 {
                     Path.Combine(transaction, "InstallUpdate.sh"), processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     target, transaction, pending
                 })
        {
            start.ArgumentList.Add(argument);
        }

        token.ThrowIfCancellationRequested();
        using Process process = Process.Start(start) ?? throw new IOException("Unable to start the update helper.");
        // Once started, the helper owns its files. Cancellation must not delete files it is using.
        _handedOff = true;
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(transaction, "ready")))
            {
                if (process.HasExited || deadline.Elapsed > TimeSpan.FromSeconds(10))
                {
                    throw new IOException("The update helper did not start. See " + Path.Combine(transaction, "install.log"));
                }

                await Task.Delay(50, token);
            }
        }
        catch
        {
            // The current app is still alive, so the helper cannot have replaced it yet.
            if (!process.HasExited)
            {
                process.Kill();
            }

            await process.WaitForExitAsync(CancellationToken.None);
            _handedOff = false;
            throw;
        }
    }

    public void Dispose()
    {
        lease.Dispose();
        if (_handedOff)
        {
            return;
        }

        File.Delete(pending);
        Directory.Delete(transaction, true);
    }
}
