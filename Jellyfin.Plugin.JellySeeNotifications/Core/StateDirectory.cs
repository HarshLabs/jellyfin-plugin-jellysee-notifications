namespace Jellyfin.Plugin.JellySeeNotifications.Core;

// Managed path checks deliberately do not promise race-free no-follow opens.
// Jellyfin's data directory is a server-process trust boundary (see docs/state-storage.md).
internal sealed class StateDirectory : IDisposable
{
    private readonly string path;
    private readonly string root;
    private readonly string resolvedRoot;
    private readonly string relative;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly string[] TempPrefixes = ["snapshot.tmp.", "journal.ndjson.tmp.", "snapshot.bin.corrupt.tmp."];
    internal const int RecoveryLimit = 128;

    internal StateDirectory(string path, string pluginDataPath)
    {
        this.path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginDataPath));
        relative = Path.GetRelativePath(root, this.path);
        if (Path.IsPathRooted(relative) || relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison))
            throw new IOException("State directory is outside plugin data path");
        resolvedRoot = ResolveDirectory(root);
        ValidateDirectory(create: true);
        RecoverTemporaryFiles();
    }
    private static void Name(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new IOException("Invalid state name");
    }
    // Resolve each ancestor too: ResolveLinkTarget on the leaf alone misses a
    // symlinked parent. The trusted root may legitimately contain OS path aliases.
    private static string ResolveDirectory(string directory)
    {
        string current = Path.GetPathRoot(directory)!;
        foreach (string part in directory[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var info = new DirectoryInfo(Path.Combine(current, part));
            info.Refresh();
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (!info.Exists || (info.Attributes & FileAttributes.Directory) == 0) throw new IOException("Missing plugin data path");
            current = target?.FullName ?? info.FullName;
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
    private static bool IsLink(FileSystemInfo info) => info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
    private void ValidateDirectory(bool create = false)
    {
        if (!string.Equals(ResolveDirectory(root), resolvedRoot, PathComparison)) throw new IOException("Plugin data path changed");
        string current = root;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            info.Refresh();
            // Check LinkTarget even for dangling links, where Exists is false.
            if (info.LinkTarget is not null || info.Exists && IsLink(info)) throw new IOException("Linked state directory");
            if (!info.Exists && create)
            {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(current);
                else Directory.CreateDirectory(current, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                info.Refresh();
            }
            if (!info.Exists || IsLink(info) || (info.Attributes & FileAttributes.Directory) == 0) throw new IOException("Unsafe state directory");
        }
        string resolved = ResolveDirectory(path);
        string boundary = Path.EndsInDirectorySeparator(resolvedRoot) ? resolvedRoot : resolvedRoot + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(boundary, PathComparison)) throw new IOException("State directory escaped plugin data path");
    }
    private string CheckedPath(string name) { Name(name); ValidateDirectory(); return Path.Combine(path, name); }
    private FileInfo? Existing(string name)
    {
        var info = new FileInfo(CheckedPath(name)); info.Refresh();
        if (info.LinkTarget is not null) throw new IOException("Linked state file");
        try
        {
            if ((File.GetAttributes(info.FullName) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("Unsafe state file");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        return info;
    }
    internal FileStream? Read(string name)
    {
        var info = Existing(name);
        // .NET 9/10 have no public link-count API. Existing hardlinks are read
        // only; Rewrite gives the journal a fresh exclusively-created file.
        return info is null ? null : new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
    }
    internal (string Name, FileStream Stream) Create(string prefix)
    {
        Name(prefix);
        string name = prefix + "." + Guid.NewGuid().ToString("N");
        string target = CheckedPath(name);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.Read | FileShare.Delete };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        // CreateNew fails for any existing entry, including dangling links.
        return (name, new FileStream(target, options));
    }
    internal void Verify(string name, FileStream stream)
    {
        var info = Existing(name) ?? throw new IOException("State name disappeared");
        if (info.Length != stream.Length) throw new IOException("State file changed");
    }
    internal void Replace(string name, FileStream stream, string target)
    {
        stream.Flush(true);
        Verify(name, stream);
        // Move overwrites the destination entry itself, including a link; it
        // never opens/truncates the destination or writes through its links.
        File.Move(CheckedPath(name), CheckedPath(target), overwrite: true);
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
        catch { temp.Stream.Dispose(); Discard(temp.Name); throw; }
    }
    internal void Remove(string name) => File.Delete(CheckedPath(name));
    // Only called after disposing an owned temporary stream. A failed cleanup
    // is retried by bounded startup recovery; never mask the original failure.
    internal void Discard(string name)
    {
        try { Remove(name); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private void RecoverTemporaryFiles()
    {
        int recovered = 0;
        foreach (string prefix in TempPrefixes)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(path, prefix + "*", SearchOption.TopDirectoryOnly))
            {
                if (++recovered > RecoveryLimit) throw new IOException("State temporary recovery limit");
                string name = Path.GetFileName(entry);
                string suffix = name[prefix.Length..];
                if (suffix.Length != 32 || !Guid.TryParseExact(suffix, "N", out _)) continue;
                string candidate = CheckedPath(name);
                // Deleting an entry never follows file hardlinks or symlinks.
                // Do not recurse into unexpected directories or reparse points.
                var info = new FileInfo(candidate); info.Refresh();
                if ((info.Attributes & FileAttributes.Directory) != 0) throw new IOException("Unexpected temporary directory");
                File.Delete(candidate);
            }
        }
    }
    internal long Length(string name) { using var input = Read(name); return input?.Length ?? 0; }
    public void Dispose() { }
}
