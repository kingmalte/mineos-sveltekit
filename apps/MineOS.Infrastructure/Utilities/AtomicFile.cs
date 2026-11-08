namespace MineOS.Infrastructure.Utilities;

/// <summary>
/// Writes a file by filling a temporary file next to it and renaming that into place.
/// </summary>
/// <remarks>
/// Writing in place (FileMode.Create) has two problems on a server host:
/// <list type="bullet">
/// <item>A failure part-way leaves the target truncated, and a running JVM that has
/// the old jar open sees its bytes change underneath it (it can crash with SIGBUS).
/// rename() swaps the directory entry in one step; the old file stays intact for
/// whoever still has it open.</item>
/// <item>It writes <em>through</em> a symbolic link sitting at the target, so a link
/// planted in a server folder (say mods/x.jar -> /etc/cron.d/x) turned a root write
/// into a write anywhere. rename() replaces the link itself instead.</item>
/// </list>
/// The temp file takes the old file's permission bits so an executable stays
/// executable; <c>beforeCommit</c> runs on the temp path (e.g. to chown it) so the
/// file is never visible with the wrong owner.
/// </remarks>
internal static class AtomicFile
{
    public static async Task WriteAsync(
        string targetPath,
        Func<Stream, CancellationToken, Task> writeContent,
        Func<string, CancellationToken, Task>? beforeCommit,
        CancellationToken cancellationToken)
    {
        var temp = TempPathFor(targetPath);
        try
        {
            await using (var stream = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await writeContent(stream, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            CopyMode(targetPath, temp);
            if (beforeCommit != null)
            {
                await beforeCommit(temp, cancellationToken);
            }

            File.Move(temp, targetPath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Synchronous variant for the (synchronous) archive extraction paths.</summary>
    public static void Write(string targetPath, Action<Stream> writeContent, Action<string>? beforeCommit)
    {
        var temp = TempPathFor(targetPath);
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                writeContent(stream);
                stream.Flush(flushToDisk: true);
            }

            CopyMode(targetPath, temp);
            beforeCommit?.Invoke(temp);
            File.Move(temp, targetPath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static string TempPathFor(string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new ArgumentException("Target path has no directory", nameof(targetPath));
        return Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.mineos-tmp");
    }

    private static void CopyMode(string targetPath, string tempPath)
    {
        // A link at the target is about to be replaced, not followed: do not copy
        // the mode of whatever it points at.
        if (OperatingSystem.IsWindows() || !File.Exists(targetPath) || new FileInfo(targetPath).LinkTarget != null)
        {
            return;
        }
        File.SetUnixFileMode(tempPath, File.GetUnixFileMode(targetPath));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort; the original error matters more.
        }
    }
}
