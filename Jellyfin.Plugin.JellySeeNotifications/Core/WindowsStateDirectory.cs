using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.JellySeeNotifications.Core;

// NT relative opens and handle-based renames give Windows the same directory
// boundary as openat/renameat. Reparse points are never traversed.
internal sealed class WindowsStateDirectory : IDisposable
{
    private readonly SafeFileHandle directory;
    internal WindowsStateDirectory(string path)
    {
        directory = CreateFileW(Path.GetFullPath(path), 0x80100000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        try { Check(directory, true); }
        catch { directory.Dispose(); throw; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes
    {
        public uint Length; public IntPtr RootDirectory, ObjectName; public uint Attributes; public IntPtr SecurityDescriptor, SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatus { public IntPtr Status, Information; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    internal static (ulong Device, ulong Inode) Check(SafeFileHandle handle, bool isDirectory = false)
    {
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info) ||
            (info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != isDirectory || !isDirectory && info.Links != 1)
            throw new IOException("Unsafe state handle");
        return (info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow);
    }
    internal FileStream? Open(string name, bool create = false, bool delete = false)
    {
        IntPtr text = Marshal.StringToHGlobalUni(name), unicode = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2)), Buffer = text }, unicode, false);
            var attributes = new ObjectAttributes { Length = (uint)Marshal.SizeOf<ObjectAttributes>(), RootDirectory = directory.DangerousGetHandle(), ObjectName = unicode, Attributes = 0x1040 }; // DONT_REPARSE | CASE_INSENSITIVE
            uint access = 0x80100000u | (create ? 0x40010000u : delete ? 0x10000u : 0);
            int status = NtCreateFile(out var handle, access, ref attributes, out _, IntPtr.Zero, 0x80, 7, create ? 2u : 1u, 0x00200060, IntPtr.Zero, 0);
            if (status < 0)
            {
                handle?.Dispose();
                if (!create && status == unchecked((int)0xc0000034)) return null; // NAME_NOT_FOUND only
                throw new IOException("Secure state open failed");
            }
            try { Check(handle); return new FileStream(handle, create ? FileAccess.ReadWrite : FileAccess.Read); }
            catch { handle.Dispose(); throw; }
        }
        finally { Marshal.FreeHGlobal(unicode); Marshal.FreeHGlobal(text); }
    }
    internal void Replace(FileStream stream, string target)
    {
        byte[] name = System.Text.Encoding.Unicode.GetBytes(target);
        int rootOffset = IntPtr.Size == 8 ? 8 : 4, lengthOffset = rootOffset + IntPtr.Size, nameOffset = lengthOffset + 4;
        IntPtr info = Marshal.AllocHGlobal(nameOffset + name.Length);
        try
        {
            for (int i = 0; i < nameOffset; i++) Marshal.WriteByte(info, i, 0);
            Marshal.WriteByte(info, 0, 1); // ReplaceIfExists
            Marshal.WriteIntPtr(info, rootOffset, directory.DangerousGetHandle());
            Marshal.WriteInt32(info, lengthOffset, name.Length); Marshal.Copy(name, 0, info + nameOffset, name.Length);
            if (!SetFileInformationByHandle(stream.SafeFileHandle, 3, info, (uint)(nameOffset + name.Length))) throw new IOException("State replacement failed");
        }
        finally { Marshal.FreeHGlobal(info); }
    }
    internal void Remove(string name)
    {
        using var stream = Open(name, delete: true) ?? throw new IOException("Missing state file");
        IntPtr info = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(info, 1);
            if (!SetFileInformationByHandle(stream.SafeFileHandle, 4, info, 4)) throw new IOException("State unlink failed");
        }
        finally { Marshal.FreeHGlobal(info); }
    }
    public void Dispose() => directory.Dispose();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, IntPtr info, uint size);
    [DllImport("ntdll.dll")] private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatus status, IntPtr allocationSize, uint fileAttributes, uint sharing, uint disposition, uint options, IntPtr eaBuffer, uint eaLength);
}
