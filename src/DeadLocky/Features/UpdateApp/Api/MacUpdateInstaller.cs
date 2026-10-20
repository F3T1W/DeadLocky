using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using DeadLocky.Features.UpdateApp.Model;

namespace DeadLocky.Features.UpdateApp.Api;

[SupportedOSPlatform("macos")]
public sealed class MacUpdateInstaller(string application, string stateDirectory)
{
    private const string BundleId = "org.milkdeadlockdry.launcher";
    private readonly string _application = Path.GetFullPath(application);
    private readonly string _state = Path.GetFullPath(stateDirectory);

    public async Task<PreparedAppUpdate> PrepareAsync(AppUpdateClient client, AppRelease release,
        IProgress<double> progress, CancellationToken token)
    {
        _ = Directory.CreateDirectory(_state);
        var lease = new FileStream(Path.Combine(_state, "update.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        string pending = Path.Combine(_state, "pending.json");
        string transaction = Path.Combine(Path.GetDirectoryName(_application)!,
            ".DeadLockyUpdate-" + Guid.NewGuid().ToString("N"));
        bool mounted = false;
        bool receiptCreated = false;
        try
        {
            if (File.Exists(pending))
            {
                throw new IOException("A previous update is still pending. Restart DeadLocky before trying again.");
            }

            if (!_application.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                || _application.StartsWith("/Volumes/", StringComparison.Ordinal)
                || _application.Contains('\n') || _application.Contains('\r'))
            {
                throw new IOException("Copy DeadLocky to Applications and open that copy before updating.");
            }

            // This also checks parent-directory permissions before downloading anything.
            _ = Directory.CreateDirectory(transaction);
            string mount = Path.Combine(transaction, "Mount");
            _ = Directory.CreateDirectory(mount);
            string dmg = Path.Combine(transaction, "Update.dmg");
            await client.DownloadAsync(release, dmg, progress, token);
            token.ThrowIfCancellationRequested();
            // Mark before attach: cancellation or an attach error can still leave a mounted image.
            mounted = true;
            _ = await UpdateProcess.RunAsync("/usr/bin/hdiutil", token,
                "attach", dmg, "-readonly", "-nobrowse", "-mountpoint", mount);
            string source = Path.Combine(mount, "DeadLocky.app");
            await ValidateBundleAsync(source, release.Version, token);
            string staged = Path.Combine(transaction, "Next.app");
            _ = await UpdateProcess.RunAsync("/usr/bin/ditto", token, source, staged);
            _ = await UpdateProcess.RunAsync("/usr/bin/hdiutil", token, "detach", mount);
            mounted = false;
            await ValidateBundleAsync(staged, release.Version, token);
            string script = Path.Combine(transaction, "InstallUpdate.sh");
            await using (Stream resource = typeof(MacUpdateInstaller).Assembly.GetManifestResourceStream(
                             "DeadLocky.InstallUpdate.sh") ?? throw new IOException("The update helper is missing."))
            await using (FileStream file = File.Create(script))
            {
                await resource.CopyToAsync(file, token);
            }

            await File.WriteAllTextAsync(Path.Combine(transaction, "original.sha256"),
                await FingerprintAsync(_application, token), token);
            var receipt = new UpdateReceipt(_application, transaction, release.Version.ToString());
            await using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                receiptCreated = true;
                await JsonSerializer.SerializeAsync(file, receipt, UpdateJsonContext.Default.UpdateReceipt, token);
            }

            return new PreparedAppUpdate(transaction, pending, _application, lease);
        }
        catch
        {
            if (receiptCreated)
            {
                File.Delete(pending);
            }

            await lease.DisposeAsync();
            if (mounted)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    _ = await UpdateProcess.RunAsync("/usr/bin/hdiutil", timeout.Token,
                        "detach", Path.Combine(transaction, "Mount"));
                    mounted = false;
                }
                catch (IOException)
                {
                    // Preserve the directory if detaching failed; never recursively delete a mounted image.
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (!mounted && Directory.Exists(transaction))
            {
                Directory.Delete(transaction, true);
            }

            throw;
        }
    }

    private async Task ValidateBundleAsync(string path, AppVersion expected, CancellationToken token)
    {
        Dictionary<string, string> values = await ReadBundleAsync(path, token);
        if (values.GetValueOrDefault("CFBundleIdentifier") != BundleId
            || AppVersion.Parse(values.GetValueOrDefault("CFBundleShortVersionString")) is not { } version
            || version.CompareTo(expected) != 0 || values.GetValueOrDefault("CFBundleExecutable") != "DeadLocky"
            || string.IsNullOrWhiteSpace(values.GetValueOrDefault("NSMicrophoneUsageDescription"))
            || !File.Exists(Path.Combine(path, "Contents", "MacOS", "DeadLocky")))
        {
            throw new IOException("The downloaded app does not match the expected DeadLocky release.");
        }

        if (!Version.TryParse(values.GetValueOrDefault("LSMinimumSystemVersion"), out Version? minimum)
            || !OperatingSystem.IsMacOSVersionAtLeast(minimum.Major, Math.Max(0, minimum.Minor),
                Math.Max(0, minimum.Build)))
        {
            throw new IOException("This release requires a newer version of macOS.");
        }

        string architectures = await UpdateProcess.RunAsync("/usr/bin/lipo", token,
            "-archs", Path.Combine(path, "Contents", "MacOS", "DeadLocky"));
        if (!architectures.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("arm64"))
        {
            throw new IOException("The update does not include an Apple Silicon executable.");
        }

        _ = await UpdateProcess.RunAsync("/usr/bin/codesign", token, "--verify", "--deep", "--strict", path);
        string installed = await UpdateProcess.RunAsync("/usr/bin/codesign", token, "-dv", _application);
        string candidate = await UpdateProcess.RunAsync("/usr/bin/codesign", token, "-dv", path);
        string? team = Team(installed);
        if (team is not null && team != Team(candidate))
        {
            throw new IOException("The update's signing team differs from the installed application.");
        }
    }

    private static string? Team(string signing)
    {
        string? value = signing.Split('\n').FirstOrDefault(static line => line.StartsWith("TeamIdentifier=", StringComparison.Ordinal));
        return value is null or "TeamIdentifier=not set" ? null : value[15..].Trim();
    }

    private static async Task<Dictionary<string, string>> ReadBundleAsync(string path, CancellationToken token)
    {
        string xml = await UpdateProcess.RunAsync("/usr/bin/plutil", token,
            "-convert", "xml1", "-o", "-", Path.Combine(path, "Contents", "Info.plist"));
        XElement[] items = XDocument.Parse(xml).Root?.Element("dict")?.Elements().ToArray()
                           ?? throw new IOException("The update has no valid Info.plist.");
        Dictionary<string, string> values = [];
        for (int i = 0; i + 1 < items.Length; i += 2)
        {
            if (items[i].Name == "key" && items[i + 1].Name == "string")
            {
                values.Add(items[i].Value, items[i + 1].Value);
            }
        }

        return values;
    }

    internal static async Task<string> FingerprintAsync(string app, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in new[] { "Contents/Info.plist", "Contents/MacOS/DeadLocky" })
        {
            await using FileStream file = File.OpenRead(Path.Combine(app, path));
            hash.AppendData(await SHA256.HashDataAsync(file, token));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public async Task ConfirmLaunchAsync(AppVersion version)
    {
        string pending = Path.Combine(_state, "pending.json");
        if (!File.Exists(pending))
        {
            return;
        }

        try
        {
            await using FileStream file = File.OpenRead(pending);
            UpdateReceipt? receipt = await JsonSerializer.DeserializeAsync(file,
                UpdateJsonContext.Default.UpdateReceipt);
            if (receipt is null || receipt.Target != _application || receipt.Version != version.ToString()
                || !ValidTransaction(receipt.Transaction, _application)
                || !File.Exists(Path.Combine(receipt.Transaction, "installed")))
            {
                return;
            }

            await File.WriteAllTextAsync(Path.Combine(receipt.Transaction, "confirmed"), "ready");
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A failed cleanup must not prevent the installed app from opening.
        }
    }

    private static bool ValidTransaction(string transaction, string target)
    {
        const string prefix = ".DeadLockyUpdate-";
        string name = Path.GetFileName(transaction);
        return Path.IsPathFullyQualified(transaction) && Path.GetDirectoryName(transaction) == Path.GetDirectoryName(target)
            && name.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(name[prefix.Length..], "N", out _);
    }
}
