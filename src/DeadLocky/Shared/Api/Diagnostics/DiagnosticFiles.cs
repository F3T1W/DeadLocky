using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeadLocky.Shared.Api.Diagnostics;

internal static partial class DiagnosticFiles
{
    public static void RequireRegularPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("A diagnostic path contains a symbolic link.");
            }
        }
    }

    public static FileStream OpenRead(string path)
    {
        RequireRegularPath(path);
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return File.OpenRead(path);
        }

        // Open every component relative to an already-open directory. A renamed ancestor cannot redirect the read.
        int noFollow = OperatingSystem.IsMacOS() ? 0x100 : 0x20000;
        int directory = OperatingSystem.IsMacOS() ? 0x100000 : 0x10000;
        int nonBlocking = OperatingSystem.IsMacOS() ? 0x4 : 0x800;
        using SafeFileHandle root = Handle(Open("/", directory | noFollow));
        SafeFileHandle current = root;
        try
        {
            string[] parts = Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < parts.Length; index++)
            {
                bool last = index == parts.Length - 1;
                SafeFileHandle next = Handle(OpenAt(current, parts[index], noFollow | (last ? nonBlocking : directory)));
                if (current != root) { current.Dispose(); }
                current = next;
            }

            var stream = new FileStream(current, FileAccess.Read);
            if (!stream.CanSeek)
            {
                stream.Dispose();
                throw new IOException("Diagnostic input is not a regular file.");
            }
            current = root; // Ownership of the final descriptor transferred to FileStream.
            return stream;
        }
        finally
        {
            if (current != root) { current.Dispose(); }
        }
    }

    private static SafeFileHandle Handle(int descriptor)
    {
        return descriptor < 0 ? throw new IOException("Unable to open a regular diagnostic file.")
            : new SafeFileHandle(descriptor, true);
    }

    public static void CreatePrivateDirectory(string path)
    {
        RequireRegularPath(path);
        _ = OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()
            ? Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)
            : Directory.CreateDirectory(path);
    }

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAt(SafeFileHandle directory, string path, int flags);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);
}
