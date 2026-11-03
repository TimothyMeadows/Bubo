using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Bubo.FloorPlan.Infrastructure;

internal static class ExportFileGuards
{
    // A default-stream SHA or directory listing cannot certify an NTFS file's complete byte inventory.
    internal static void NoAlternateStreams(string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return;
        path = Path.GetFullPath(path);
        if (!path.StartsWith("\\\\?\\", StringComparison.Ordinal))
            path = path.StartsWith("\\\\", StringComparison.Ordinal) ? "\\\\?\\UNC\\" + path[2..] : "\\\\?\\" + path;
        var handle = FindFirstStreamW(path, 0, out var data, 0);
        if (handle == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 38) return; // ERROR_HANDLE_EOF: directories may have no data stream.
            throw new IOException("Cannot verify closed export stream inventory", new Win32Exception(error));
        }
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!string.Equals(data.Name, "::$DATA", StringComparison.Ordinal))
                    throw new InvalidOperationException("Named alternate data stream in export state or payload");
                if (FindNextStreamW(handle, out data)) continue;
                var error = Marshal.GetLastWin32Error();
                if (error != 38) throw new IOException("Cannot finish closed export stream inventory", new Win32Exception(error));
                break;
            }
        }
        finally { _ = FindClose(handle); }
        cancellation.ThrowIfCancellationRequested();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StreamData
    {
        public long Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    static extern IntPtr FindFirstStreamW(string fileName, int level, out StreamData data, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FindNextStreamW(IntPtr handle, out StreamData data);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FindClose(IntPtr handle);
}
