using System.IO.Compression;
using System.Text.Json;
using DeadLocky.Features.ReportProblem.Model;
using DeadLocky.Shared.Api.Diagnostics;

namespace DeadLocky.Features.ReportProblem.Api;

public sealed class ReportArchive(DiagnosticLog log, string temporaryRoot)
{
    public const int MaximumBytes = 8 * 1024 * 1024;

    public async Task<string> CreateAsync(ProblemReport report, CancellationToken token)
    {
        ProblemReport.Validate(report.Message, report.Email);
        LogSnapshot[] snapshot = await log.SnapshotAsync(token);
        return await Task.Run(async () =>
        {
            DiagnosticFiles.RequireRegularPath(temporaryRoot);
            DiagnosticFiles.CreatePrivateDirectory(temporaryRoot);
            CleanupExpired();
            string directory = Path.Combine(temporaryRoot, "report-" + Guid.NewGuid().ToString("N"));
            DiagnosticFiles.CreatePrivateDirectory(directory);
            string path = Path.Combine(directory, $"DeadLocky-report-{report.Id:D}.zip");
            try
            {
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous);
                await using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    var manifest = new ReportManifest(report,
                        "Serialized log-writer barrier; log-only snapshot. No settings, game files, mods or crash reports.",
                        [.. snapshot.Select(static file => new ReportLogFile(file.Name, file.Content.Length, file.Truncated))]);
                    await using (Stream entry = await archive.CreateEntry("report.json").OpenAsync(token))
                    {
                        await JsonSerializer.SerializeAsync(entry, manifest, ReportJsonContext.Default.ReportManifest, token);
                    }

                    int total = 0;
                    foreach (LogSnapshot file in snapshot)
                    {
                        token.ThrowIfCancellationRequested();
                        total += file.Content.Length;
                        if (snapshot.Length > 5 || file.Content.Length > 2 * 1024 * 1024 || total > 6 * 1024 * 1024
                            || Path.GetFileName(file.Name) != file.Name)
                        {
                            throw new IOException("Diagnostic snapshot exceeds the report limits.");
                        }

                        await using Stream entry = await archive.CreateEntry("Logs/" + file.Name, CompressionLevel.Fastest).OpenAsync(token);
                        await entry.WriteAsync(file.Content, token);
                    }
                }

                await output.FlushAsync(token);
                token.ThrowIfCancellationRequested();
                return output.Length > MaximumBytes ? throw new IOException("The diagnostic ZIP exceeds 8 MiB.") : path;
            }
            catch
            {
                Delete(path);
                throw;
            }
        }, token);
    }

    public static async Task SaveAsync(string source, string destination, CancellationToken token)
    {
        await Task.Run(async () =>
        {
            DiagnosticFiles.RequireRegularPath(destination);
            string staging = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                await using (FileStream input = DiagnosticFiles.OpenRead(source))
                await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await input.CopyToAsync(output, token);
                }
                token.ThrowIfCancellationRequested();
                File.Move(staging, destination, true);
            }
            finally { File.Delete(staging); }
        }, token);
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
            Directory.Delete(Path.GetDirectoryName(path)!);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void CleanupExpired()
    {
        foreach (string directory in Directory.EnumerateDirectories(temporaryRoot, "report-*").Take(100))
        {
            DiagnosticFiles.RequireRegularPath(directory);
            if (!Guid.TryParseExact(Path.GetFileName(directory)[7..], "N", out _)
                || Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow.AddDays(-1))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(directory, "DeadLocky-report-*.zip"))
            {
                DiagnosticFiles.RequireRegularPath(file);
                File.Delete(file);
            }

            if (!Directory.EnumerateFileSystemEntries(directory).Any()) { Directory.Delete(directory); }
        }
    }
}
