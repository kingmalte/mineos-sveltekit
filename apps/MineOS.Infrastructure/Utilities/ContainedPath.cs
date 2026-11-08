namespace MineOS.Infrastructure.Utilities;

/// <summary>
/// Resolves a user-supplied path inside a root directory the way the kernel will
/// see it — symbolic links followed — and refuses anything that ends up outside.
/// </summary>
/// <remarks>
/// A purely textual check (<see cref="Path.GetFullPath(string)"/> plus a prefix
/// compare) is not enough: the API runs as root, and a symlink inside a server
/// directory — left by a plugin, a mod, or an extracted archive — let the file
/// manager read and write anywhere on the host (and chown what it wrote, which
/// handed directories like /etc to the server user). Callers must then use the
/// returned path, never the one they were given, so the checked path is the one
/// that is opened.
///
/// What remains is a race: something running as the server user could swap a
/// directory for a symlink between this check and the open. Closing that would
/// need openat/O_NOFOLLOW, which .NET does not expose.
/// </remarks>
internal static class ContainedPath
{
    private const int MaxLinkDepth = 40;

    /// <summary>
    /// The real path of <paramref name="path"/>: every symbolic link along it is
    /// followed. Components that do not exist yet are kept as given, so this also
    /// works for a path about to be created.
    /// </summary>
    public static string ResolveReal(string path) => ResolveReal(path, 0);

    private static string ResolveReal(string path, int depth)
    {
        if (depth > MaxLinkDepth)
        {
            throw new IOException("Too many levels of symbolic links");
        }

        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "/";
        var parts = full[root.Length..].Split(
            Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            // LinkTarget comes from lstat: null for a regular file or directory and
            // for a path that does not exist, the raw target text for a link (also a
            // dangling one).
            var target = new FileInfo(next).LinkTarget;
            if (target != null)
            {
                var targetPath = Path.IsPathRooted(target) ? target : Path.Combine(current, target);
                var rest = string.Join(Path.DirectorySeparatorChar, parts[(i + 1)..]);
                return ResolveReal(rest.Length > 0 ? Path.Combine(targetPath, rest) : targetPath, depth + 1);
            }
            current = next;
        }

        return current;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or below it.</summary>
    public static bool IsWithin(string root, string path)
    {
        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar);
        return path == trimmedRoot
            || path.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves <paramref name="relativePath"/> under <paramref name="root"/>.
    /// </summary>
    /// <param name="followFinal">
    /// Follow a symlink in the last component too (to read or write what it points
    /// at). Off for operations on the entry itself — deleting or moving a link must
    /// act on the link, not on its target.
    /// </param>
    /// <exception cref="ArgumentException">The path leads outside the root.</exception>
    public static string Resolve(string root, string relativePath, bool followFinal)
    {
        var realRoot = ResolveReal(root);
        var lexical = Path.GetFullPath(Path.Combine(realRoot, relativePath.TrimStart('/', '\\')));

        if (!IsWithin(realRoot, lexical))
        {
            throw new ArgumentException("Invalid path - directory traversal detected");
        }
        if (lexical == realRoot)
        {
            return realRoot;
        }

        var parent = ResolveReal(Path.GetDirectoryName(lexical)!);
        if (!IsWithin(realRoot, parent))
        {
            throw OutsideRoot();
        }

        var entry = Path.Combine(parent, Path.GetFileName(lexical));
        if (!followFinal)
        {
            return entry;
        }

        var real = ResolveReal(entry);
        if (!IsWithin(realRoot, real))
        {
            throw OutsideRoot();
        }
        return real;
    }

    private static ArgumentException OutsideRoot() =>
        new("Invalid path - it leads outside the server directory (through a symbolic link)");
}
