using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DeadLocky.Features.LaunchGame.Model;
using DeadLocky.Shared.Api.Diagnostics;

namespace DeadLocky.Features.LaunchGame.Api;

internal static partial class MacGameProcessScanner
{
    public static async Task StopPrefixProcessesAsync(GameLaunchConfiguration configuration, CancellationToken token)
    {
        string prefix = CanonicalPath(configuration.PrefixDirectory);
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    (string[] arguments, string[] environment) = ReadArguments(process.Id);
                    string? processPrefix = environment.FirstOrDefault(static value => value.StartsWith("WINEPREFIX=",
                        StringComparison.Ordinal))?["WINEPREFIX=".Length..];
                    bool wine = process.ProcessName.Contains("wine", StringComparison.OrdinalIgnoreCase)
                                || arguments.Any(static value =>
                                    value.Trim('"').EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                    if (!wine || processPrefix is null || CanonicalPath(processPrefix) != prefix)
                    {
                        continue;
                    }

                    AppLog.Event("process.prefix-stop", "terminating owned prefix process", fields: AppLog.Data(("process.id", process.Id)));
                    process.Kill();
                    await process.WaitForExitAsync(token);
                }
                catch (InvalidOperationException)
                {
                }
                catch (Win32Exception exception)
                {
                    if (!process.HasExited)
                    {
                        throw new IOException("Unable to stop a leftover process in the GPTK prefix.", exception);
                    }
                }
            }
        }
    }

    public static bool IsRunning(GameLaunchConfiguration configuration, string directory)
    {
        return FindProcessId(configuration, directory) is not null;
    }

    public static int? FindProcessId(GameLaunchConfiguration configuration, string directory)
    {
        string expected = CanonicalPath(Path.Combine(directory, "game", "bin", "win64", "deadlock.exe"));
        string prefix = CanonicalPath(configuration.PrefixDirectory);
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    string name = process.ProcessName;
                    if (!name.Contains("deadlock", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("wine", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    (string[] arguments, string[] environment) = ReadArguments(process.Id);
                    string? processPrefix = environment.FirstOrDefault(static value => value.StartsWith("WINEPREFIX=",
                        StringComparison.Ordinal))?["WINEPREFIX=".Length..];
                    if (processPrefix is null || CanonicalPath(processPrefix) != prefix)
                    {
                        continue;
                    }

                    if (arguments.Select(argument => MapGameArgument(argument, configuration.PrefixDirectory))
                        .Any(path => path is not null && CanonicalPath(path) == expected))
                    {
                        return process.Id;
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException
                                                      or Win32Exception or IOException or ArgumentException)
                {
                }
            }
        }

        return null;
    }

    private static string? MapGameArgument(string argument, string prefix)
    {
        string value = argument.Trim('"').Replace('\\', '/');
        return value switch
        {
            _ when !value.EndsWith("/deadlock.exe", StringComparison.OrdinalIgnoreCase) => null,
            _ when value.StartsWith("Z:/", StringComparison.OrdinalIgnoreCase) => value[2..],
            _ when value.StartsWith("C:/", StringComparison.OrdinalIgnoreCase)
                => Path.Combine(prefix, "drive_c", value[3..]),
            _ when Path.IsPathFullyQualified(value) => value,
            _ => null
        };
    }

    internal static string CanonicalPath(string path)
    {
        IntPtr resolved = RealPath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
        {
            return Path.GetFullPath(path);
        }

        try
        {
            return Marshal.PtrToStringUTF8(resolved) ?? Path.GetFullPath(path);
        }
        finally
        {
            Free(resolved);
        }
    }

    private static unsafe (string[] Arguments, string[] Environment) ReadArguments(int pid)
    {
        int[] query = [1, 49, pid];
        nuint length = 0;
        byte[] buffer;
        fixed (int* queryPointer = query)
        {
            if (Sysctl((nint)queryPointer, 3, 0, ref length, 0, 0) != 0 || length < 5 || length > 1024 * 1024)
            {
                return ([], []);
            }

            buffer = new byte[(int)length];
            fixed (byte* bufferPointer = buffer)
            {
                if (Sysctl((nint)queryPointer, 3, (nint)bufferPointer, ref length, 0, 0) != 0)
                {
                    return ([], []);
                }
            }
        }

        int argc = BitConverter.ToInt32(buffer, 0);
        int position = 4;
        _ = ReadString(buffer, ref position);
        while (position < (int)length && buffer[position] == 0)
        {
            position++;
        }

        var arguments = new List<string>();
        for (int index = 0; index < argc && position < (int)length; index++)
        {
            arguments.Add(ReadString(buffer, ref position));
        }

        var environment = new List<string>();
        while (position < (int)length)
        {
            string entry = ReadString(buffer, ref position);
            if (entry.Length > 0)
            {
                environment.Add(entry);
            }
        }

        return ([.. arguments], [.. environment]);
    }

    private static string ReadString(byte[] bytes, ref int position)
    {
        int start = position;
        while (position < bytes.Length && bytes[position] != 0)
        {
            position++;
        }

        string value = Encoding.UTF8.GetString(bytes, start, position - start);
        if (position < bytes.Length)
        {
            position++;
        }

        return value;
    }

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "sysctl", SetLastError = true)]
    private static partial int Sysctl(nint name, uint nameLength, nint oldValue, ref nuint oldLength,
        IntPtr newValue, nuint newLength);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "realpath", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr RealPath(string path, IntPtr resolved);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "free")]
    private static partial void Free(IntPtr memory);
}
