using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.JellySeeNotifications.Core;

// All file names are relative to a held directory descriptor. Never reopen a path
// for writing: create an exclusive inode, verify its handle, then renameat it.
// Unsupported platforms fail closed rather than falling back to path checks.
internal sealed class StateDirectory : IDisposable
{
    private readonly SafeFileHandle directory = new(IntPtr.Zero, false);
    private readonly WindowsStateDirectory? windows;
    private static bool Mac => OperatingSystem.IsMacOS();
    private static int NoFollow => Mac ? 0x100 : 0x20000;
    private static int DirectoryFlag => Mac ? 0x100000 : 0x10000;
    private static int CloseExec => Mac ? 0x1000000 : 0x80000;
    private static int NonBlock => Mac ? 4 : 0x800;
    private static int CreateExclusive => Mac ? 0xa00 : 0xc0;
    private int Descriptor => directory.DangerousGetHandle().ToInt32();

    internal StateDirectory(string path)
    {
        if (!OperatingSystem.IsLinux() && !Mac && !OperatingSystem.IsWindows()) throw new IOException("Secure state storage is unsupported on this platform");
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows()) { windows = new WindowsStateDirectory(path); return; }
        directory = Handle(open(path, DirectoryFlag | NoFollow | CloseExec, 0));
        try { Check(directory, isDirectory: true); }
        catch { directory.Dispose(); throw; }
    }
    private static void Name(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.Contains('/') || name.Contains('\\')) throw new IOException("Invalid state name");
    }
    private static SafeFileHandle Handle(int fd) => fd < 0 ? throw new IOException("Secure state operation failed") : new SafeFileHandle((IntPtr)fd, true);
    private static (ulong Device, ulong Inode) Check(SafeFileHandle handle, bool isDirectory = false)
    {
        if (OperatingSystem.IsWindows()) return WindowsStateDirectory.Check(handle, isDirectory);
        // stat layouts for Darwin, Linux x64 and Linux arm64. Reject other ABIs.
        if (!Mac && RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)) throw new IOException("Unsupported state ABI");
        byte[] stat = new byte[256];
        if (fstat(handle, stat) != 0) throw new IOException("State handle inspection failed");
        uint mode = Mac ? BitConverter.ToUInt16(stat, 4) : BitConverter.ToUInt32(stat, RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 24 : 16);
        ulong links = Mac ? BitConverter.ToUInt16(stat, 6) : RuntimeInformation.ProcessArchitecture == Architecture.X64 ? BitConverter.ToUInt64(stat, 16) : BitConverter.ToUInt32(stat, 20);
        if ((mode & 0xf000) != (isDirectory ? 0x4000 : 0x8000) || !isDirectory && links != 1) throw new IOException("Unsafe state handle");
        return (Mac ? BitConverter.ToUInt32(stat, 0) : BitConverter.ToUInt64(stat, 0), BitConverter.ToUInt64(stat, 8));
    }
    internal FileStream? Read(string name)
    {
        Name(name);
        if (windows is not null) return windows.Open(name);
        int fd = openat(Descriptor, name, NoFollow | NonBlock | CloseExec, 0);
        if (fd < 0 && Marshal.GetLastPInvokeError() == 2) return null;
        var handle = Handle(fd);
        try { Check(handle); return new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }
    internal (string Name, FileStream Stream) Create(string prefix)
    {
        Name(prefix);
        string name = prefix + "." + Guid.NewGuid().ToString("N");
        if (windows is not null) return (name, windows.Open(name, create: true)!);
        // Darwin arm64 puts variadic arguments on the stack, unlike fixed
        // arguments. The extra register slots place mode_t in that stack slot.
        int flags = 2 | CreateExclusive | NoFollow | CloseExec;
        int fd = Mac && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? openatCreateArm64(Descriptor, name, flags, 0, 0, 0, 0, 0, 0x180)
            : openat(Descriptor, name, flags, 0x180);
        var handle = Handle(fd); // 0600
        try { Check(handle); return (name, new FileStream(handle, FileAccess.ReadWrite)); }
        catch { handle.Dispose(); throw; }
    }
    internal void Verify(string name, FileStream stream)
    {
        var identity = Check(stream.SafeFileHandle);
        using var named = Read(name) ?? throw new IOException("State name disappeared");
        if (Check(named.SafeFileHandle) != identity) throw new IOException("State name changed");
    }
    internal void Replace(string name, FileStream stream, string target)
    {
        Name(target); stream.Flush(true); Verify(name, stream);
        if (windows is not null) { windows.Replace(stream, target); Verify(target, stream); return; }
        // renameat replaces a directory entry itself; a destination symlink or
        // hardlink is unlinked, never dereferenced or truncated. A source swap
        // can only rename that entry; it cannot redirect any held-handle writes.
        if (renameat(Descriptor, name, Descriptor, target) != 0) throw new IOException("State replacement failed");
        Verify(target, stream);
        if (fsync(Descriptor) != 0) throw new IOException("State directory flush failed");
    }
    internal FileStream Rewrite(string target, FileStream? input, long length)
    {
        var temp = Create(target + ".tmp");
        try
        {
            if (input is not null)
            {
                input.Position = 0; byte[] buffer = new byte[64 * 1024];
                while (length > 0)
                {
                    int read = input.Read(buffer, 0, (int)Math.Min(length, buffer.Length));
                    if (read == 0) throw new IOException("State source truncated");
                    temp.Stream.Write(buffer, 0, read); length -= read;
                }
            }
            Replace(temp.Name, temp.Stream, target); return temp.Stream;
        }
        catch { temp.Stream.Dispose(); throw; }
    }
    internal void Remove(string name)
    {
        Name(name);
        if (windows is not null) { windows.Remove(name); return; }
        if (unlinkat(Descriptor, name, 0) != 0) throw new IOException("State unlink failed");
        if (fsync(Descriptor) != 0) throw new IOException("State directory flush failed");
    }
    internal long Length(string name) { using var input = Read(name); return input?.Length ?? 0; }
    public void Dispose() { windows?.Dispose(); directory.Dispose(); }

    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int openat(int fd, string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)] private static extern int openatCreateArm64(int fd, string path, int flags, nuint p3, nuint p4, nuint p5, nuint p6, nuint p7, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int renameat(int fromFd, string from, int toFd, string to);
    [DllImport("libc", SetLastError = true)] private static extern int fstat(SafeFileHandle fd, [Out] byte[] stat);
    [DllImport("libc", SetLastError = true)] private static extern int unlinkat(int fd, string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(int fd);
}
