using Microsoft.Win32.SafeHandles;

namespace Codexish.Server;

[Flags]
public enum Grant { None = 0, Read = 1, Write = 2, Shell = 4 }

public sealed record Target(RootConfig Root, string FullPath, string Relative, string RootFinalPath);

// D5: inputs are root_id plus a relative path. Resolution normalizes first, then every opened handle is
// compared against the root's own final path, so a path swapped between the check and the open is caught.
public sealed class Workspace
{
    private readonly ServerConfig config;
    private readonly Dictionary<string, string> finalPaths = new(StringComparer.OrdinalIgnoreCase);

    public Workspace(ServerConfig config)
    {
        this.config = config;
        foreach (var root in config.Roots) finalPaths[root.Id] = ResolveFinalPath(root.Path);
    }

    public string FinalPathOf(string rootId) => finalPaths[rootId];

    private static string ResolveFinalPath(string path)
    {
        using SafeFileHandle? handle = Native.OpenDirectory(path);
        string? final = handle is null ? null : Native.FinalPath(handle);
        return System.IO.Path.TrimEndingDirectorySeparator(final ?? System.IO.Path.GetFullPath(path));
    }

    public Target Resolve(string rootId, string? relative, Grant required)
    {
        if (string.IsNullOrWhiteSpace(rootId))
            throw new CodexishFault("INVALID_ARGUMENT", "root_id is required. Call workspace_info for the configured roots.");
        var root = config.Root(rootId);
        Grant granted = (root.Read ? Grant.Read : 0) | (root.Write ? Grant.Write : 0) | (root.Shell ? Grant.Shell : 0);
        if ((granted & required) != required)
            throw new CodexishFault("PERMISSION_DENIED",
                $"Root '{root.Id}' does not grant {required.ToString().ToLowerInvariant()}. The local user changes grants in codexish.json.",
                details: new { root_id = root.Id, granted = granted.ToString().ToLowerInvariant() });

        string rel = (relative ?? string.Empty).Trim();
        if (rel is "." or "./" or ".\\") rel = string.Empty;
        if (rel.Any(char.IsControl))
            throw new CodexishFault("INVALID_ARGUMENT", "Paths must not contain control characters.");
        if (rel.Length > 0)
        {
            if (rel.StartsWith(@"\\", StringComparison.Ordinal) || rel.StartsWith("//", StringComparison.Ordinal))
                throw new CodexishFault("OUTSIDE_WORKSPACE", "UNC paths are not addressable through a root; use root_id plus a relative path.");
            if (System.IO.Path.IsPathRooted(rel) || rel.Contains(':'))
                throw new CodexishFault("OUTSIDE_WORKSPACE", "Absolute paths and drive qualifiers are rejected; use a path relative to the root.");
            foreach (string segment in rel.Split('/', '\\'))
                if (segment == "..")
                    throw new CodexishFault("OUTSIDE_WORKSPACE", "Parent traversal ('..') leaves the granted root.");
        }

        string full = System.IO.Path.TrimEndingDirectorySeparator(
            System.IO.Path.GetFullPath(System.IO.Path.Combine(root.Path, rel)));
        if (!Inside(root.Path, full))
            throw new CodexishFault("OUTSIDE_WORKSPACE", "The normalized path leaves the granted root.");
        RejectReparse(root.Path, full);
        return new Target(root, full, rel.Replace('\\', '/'), finalPaths[root.Id]);
    }

    private static bool Inside(string root, string candidate) =>
        candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // Walks from the target up to (not past) the root: a junction or symlink inside the root is refused
    // rather than silently followed, exactly as P0 does.
    private static void RejectReparse(string root, string full)
    {
        for (string? probe = full; probe is not null && probe.Length >= root.Length; probe = System.IO.Path.GetDirectoryName(probe))
        {
            if (probe.Equals(root, StringComparison.OrdinalIgnoreCase)) return;
            if ((File.Exists(probe) || Directory.Exists(probe)) &&
                (File.GetAttributes(probe) & FileAttributes.ReparsePoint) != 0)
                throw new CodexishFault("UNSUPPORTED_CAPABILITY",
                    "Reparse points (symlinks, junctions) inside a root are not followed by file tools.");
        }
    }

    private static void VerifyHandle(SafeFileHandle handle, Target target, bool directory)
    {
        if (!OperatingSystem.IsWindows()) return; // Linux keeps the normalized-prefix guarantee only.
        string? final = Native.FinalPath(handle);
        if (final is null)
            throw new CodexishFault("OUTSIDE_WORKSPACE", "The opened handle's real path could not be verified.");
        final = System.IO.Path.TrimEndingDirectorySeparator(final);
        bool ok = directory && final.Equals(target.RootFinalPath, StringComparison.OrdinalIgnoreCase);
        ok |= final.StartsWith(target.RootFinalPath + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!ok)
            throw new CodexishFault("OUTSIDE_WORKSPACE", "The opened handle resolves outside the granted root.");
    }

    public FileStream OpenRead(Target target)
    {
        var stream = Open(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try { VerifyHandle(stream.SafeFileHandle, target, false); return stream; }
        catch { stream.Dispose(); throw; }
    }

    // The replace path reuses P0's exclusive handle: hash check and write happen on one handle nobody else holds.
    public FileStream OpenExclusive(Target target)
    {
        var stream = Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try { VerifyHandle(stream.SafeFileHandle, target, false); return stream; }
        catch { stream.Dispose(); throw; }
    }

    // Create mode: the target does not exist yet, so the fence is proved on the nearest existing parent,
    // then re-proved on the created file; a file that lands outside the root is deleted immediately.
    public FileStream CreateNew(Target target)
    {
        string parent = System.IO.Path.GetDirectoryName(target.FullPath)
            ?? throw new CodexishFault("OUTSIDE_WORKSPACE", "The target has no parent directory inside the root.");
        VerifyDirectory(target, parent);
        var stream = Open(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try { VerifyHandle(stream.SafeFileHandle, target, false); return stream; }
        catch
        {
            stream.Dispose();
            try { File.Delete(target.FullPath); } catch (IOException) { }
            throw;
        }
    }

    public void VerifyDirectory(Target target, string? path = null)
    {
        path ??= target.FullPath;
        if (!Directory.Exists(path))
            throw new CodexishFault("NOT_FOUND", $"Directory '{target.Relative}' does not exist inside root '{target.Root.Id}'.");
        if (!OperatingSystem.IsWindows()) return;
        using SafeFileHandle? handle = Native.OpenDirectory(path);
        if (handle is null)
            throw new CodexishFault("OUTSIDE_WORKSPACE", "The directory handle could not be opened for verification.");
        VerifyHandle(handle, target, true);
    }

    private static FileStream Open(Target target, FileMode mode, FileAccess access, FileShare share)
    {
        try { return new FileStream(target.FullPath, mode, access, share); }
        catch (IOException e) when (mode == FileMode.CreateNew && (e.HResult & 0xffff) is 80 or 183)
        {
            throw new CodexishFault("FILE_CHANGED",
                $"'{target.Relative}' already exists. Use mode=replace with expected_sha256 to change it.");
        }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33)
        {
            throw new CodexishFault("FILE_LOCKED",
                $"Another handle holds '{target.Relative}' with incompatible sharing. Reread after it is released.");
        }
    }
}
