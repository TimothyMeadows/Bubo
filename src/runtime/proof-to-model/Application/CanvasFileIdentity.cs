using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Hephaera.ProofToModel.Application;

/// <summary>Additional Windows hardlink check for exact registered CUDA/tool files.</summary>
public static class CanvasFileIdentity
{
    public static void RequireReadOnlyRoot(Bubo.Host.HostExecutionContext host, string root)
    {
        host.EnsureReadAllowed(root);
        var rejected = false;
        try { host.EnsureWriteAllowed(root); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("Prior state/staging and sealed bundle must be input-only directories, not a single protected probe file");
    }
    public static void NoHardLinks(string path)
    {
        if (!OperatingSystem.IsWindows()) return; // External Windows CUDA prerequisites refuse separately.
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Canvas dependency reparse point refused");
        using var stream = File.OpenRead(path);
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var information) || information.NumberOfLinks != 1)
            throw new InvalidOperationException("Canvas dependency must be a single-link ordinary file");
    }
    [StructLayout(LayoutKind.Sequential)]
    struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
