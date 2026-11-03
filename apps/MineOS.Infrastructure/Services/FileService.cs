using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MineOS.Application.Dtos;
using MineOS.Application.Interfaces;
using MineOS.Application.Options;
using MineOS.Infrastructure.Utilities;

namespace MineOS.Infrastructure.Services;

public sealed class FileService : IFileService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ILogger<FileService> _logger;
    private readonly HostOptions _hostOptions;

    public FileService(
        ILogger<FileService> logger,
        IOptions<HostOptions> hostOptions)
    {
        _logger = logger;
        _hostOptions = hostOptions.Value;
    }

    private string GetServerPath(string serverName) =>
        Path.Combine(_hostOptions.BaseDirectory, _hostOptions.ServersPathSegment, serverName);

    private string GetServerRoot(string serverName) => ContainedPath.ResolveReal(GetServerPath(serverName));

    /// <summary>The real path to read or write: links followed, guaranteed inside the server.</summary>
    private string ResolveTarget(string serverName, string relativePath) =>
        ContainedPath.Resolve(GetServerPath(serverName), relativePath, followFinal: true);

    /// <summary>The entry itself (a link stays a link), inside the server.</summary>
    private string ResolveEntry(string serverName, string relativePath) =>
        ContainedPath.Resolve(GetServerPath(serverName), relativePath, followFinal: false);

    /// <summary>
    /// Refuses operations that would replace or remove the server directory itself.
    /// "/" — or anything that normalizes to it, like " " once trimmed or "a/.." —
    /// used to reach Directory.Delete(serverDir, recursive: true).
    /// </summary>
    private static void EnsureNotRoot(string serverRoot, string resolved, string verb)
    {
        if (resolved == serverRoot)
        {
            throw new ArgumentException($"The server directory itself cannot be {verb}.");
        }
    }

    public Task<IReadOnlyList<FileEntryDto>> ListFilesAsync(string serverName, string path, CancellationToken cancellationToken)
    {
        var fullPath = ResolveTarget(serverName, path);

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {path}");
        }

        var entries = new List<FileEntryDto>();

        foreach (var dir in Directory.GetDirectories(fullPath))
        {
            var dirInfo = new DirectoryInfo(dir);
            entries.Add(new FileEntryDto(dirInfo.Name, true, 0, dirInfo.LastWriteTimeUtc));
        }

        foreach (var file in Directory.GetFiles(fullPath))
        {
            var fileInfo = new FileInfo(file);
            // A dangling link reports no length; list it rather than fail the folder.
            var length = fileInfo.Exists ? fileInfo.Length : 0;
            entries.Add(new FileEntryDto(fileInfo.Name, false, length, fileInfo.LastWriteTimeUtc));
        }

        IReadOnlyList<FileEntryDto> ordered = entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult(ordered);
    }

    public async Task<FileContentDto> ReadFileAsync(string serverName, string path, CancellationToken cancellationToken)
    {
        var fullPath = ResolveTarget(serverName, path);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"File not found: {path}");
        }

        var fileInfo = new FileInfo(fullPath);
        // Limit file size to 1MB for safety
        if (fileInfo.Length > 1024 * 1024)
        {
            throw new InvalidOperationException("File too large to open in the editor (max 1MB) - download it instead");
        }

        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
        return new FileContentDto(path, content, fileInfo.Length, fileInfo.LastWriteTimeUtc);
    }

    public async Task<byte[]> ReadFileBytesAsync(string serverName, string path, CancellationToken cancellationToken)
    {
        var fullPath = ResolveTarget(serverName, path);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"File not found: {path}");
        }

        var fileInfo = new FileInfo(fullPath);
        // Limit file size to 5MB for images
        if (fileInfo.Length > 5 * 1024 * 1024)
        {
            throw new InvalidOperationException("File too large to read (max 5MB)");
        }

        return await File.ReadAllBytesAsync(fullPath, cancellationToken);
    }

    public Task WriteFileAsync(string serverName, string path, string content, CancellationToken cancellationToken) =>
        WriteAtomicAsync(serverName, path, async (stream, ct) =>
        {
            await using var writer = new StreamWriter(stream, Utf8NoBom, leaveOpen: true);
            await writer.WriteAsync(content.AsMemory(), ct);
        }, cancellationToken);

    public Task WriteFileBytesAsync(string serverName, string path, byte[] content, CancellationToken cancellationToken) =>
        WriteAtomicAsync(serverName, path, (stream, ct) => stream.WriteAsync(content, ct).AsTask(), cancellationToken);

    public Task WriteFileStreamAsync(string serverName, string path, Stream content, CancellationToken cancellationToken) =>
        WriteAtomicAsync(serverName, path, (stream, ct) => content.CopyToAsync(stream, ct), cancellationToken);

    /// <summary>
    /// Writes to a temporary file next to the target and renames it into place.
    /// </summary>
    /// <remarks>
    /// Writing in place truncated the file first, so a failure part-way (disk full,
    /// API restart, a dropped upload) left server.properties or a plugin jar empty or
    /// cut off. rename() replaces the target in one step: readers — including a
    /// running server — see the old file or the new one. The temp file lives in the
    /// same directory so the rename never crosses filesystems, and it takes the old
    /// file's permissions so an executable start script stays executable.
    /// </remarks>
    private async Task WriteAtomicAsync(
        string serverName,
        string path,
        Func<Stream, CancellationToken, Task> writeContent,
        CancellationToken cancellationToken)
    {
        var serverRoot = GetServerRoot(serverName);
        var target = ResolveTarget(serverName, path);
        EnsureNotRoot(serverRoot, target, "overwritten");

        if (Directory.Exists(target))
        {
            throw new InvalidOperationException($"'{path}' is a directory");
        }

        var directory = Path.GetDirectoryName(target)!;
        await EnsureDirectoryAsync(serverRoot, directory, cancellationToken);

        var temp = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.mineos-tmp");
        try
        {
            await using (var stream = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await writeContent(stream, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(target) && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, File.GetUnixFileMode(target));
            }
            await ChownAsync(temp, cancellationToken);

            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        _logger.LogInformation("Wrote file {Path} for server {ServerName}", path, serverName);
    }

    public Task DeleteFileAsync(string serverName, string path, CancellationToken cancellationToken)
    {
        var serverRoot = GetServerRoot(serverName);
        var entry = ResolveEntry(serverName, path);
        EnsureNotRoot(serverRoot, entry, "deleted");

        var info = new FileInfo(entry);
        if (info.LinkTarget != null)
        {
            // Remove the link only; never what it points at.
            File.Delete(entry);
            _logger.LogInformation("Deleted link {Path} for server {ServerName}", path, serverName);
        }
        else if (File.Exists(entry))
        {
            File.Delete(entry);
            _logger.LogInformation("Deleted file {Path} for server {ServerName}", path, serverName);
        }
        else if (Directory.Exists(entry))
        {
            // Directory.Delete removes links inside the tree without following them.
            Directory.Delete(entry, recursive: true);
            _logger.LogInformation("Deleted directory {Path} for server {ServerName}", path, serverName);
        }
        else
        {
            throw new FileNotFoundException($"File or directory not found: {path}");
        }

        return Task.CompletedTask;
    }

    public async Task CreateFileAsync(string serverName, string path, CancellationToken cancellationToken)
    {
        var serverRoot = GetServerRoot(serverName);
        var target = ResolveEntry(serverName, path);
        EnsureNotRoot(serverRoot, target, "replaced");
        EnsureFree(target, path);

        await EnsureDirectoryAsync(serverRoot, Path.GetDirectoryName(target)!, cancellationToken);
        await using (new FileStream(target, FileMode.CreateNew, FileAccess.Write))
        {
        }
        await ChownAsync(target, cancellationToken);
        _logger.LogInformation("Created file {Path} for server {ServerName}", path, serverName);
    }

    public async Task CreateDirectoryAsync(string serverName, string path, CancellationToken cancellationToken)
    {
        var serverRoot = GetServerRoot(serverName);
        var target = ResolveEntry(serverName, path);
        EnsureNotRoot(serverRoot, target, "replaced");
        EnsureFree(target, path);

        await EnsureDirectoryAsync(serverRoot, target, cancellationToken);
        _logger.LogInformation("Created directory {Path} for server {ServerName}", path, serverName);
    }

    public async Task MoveAsync(string serverName, string from, string to, bool overwrite, CancellationToken cancellationToken)
    {
        var serverRoot = GetServerRoot(serverName);
        var source = ResolveEntry(serverName, from);
        var destination = ResolveEntry(serverName, to);
        EnsureNotRoot(serverRoot, source, "moved");
        EnsureNotRoot(serverRoot, destination, "replaced");

        var sourceIsLink = new FileInfo(source).LinkTarget != null;
        var sourceIsDirectory = !sourceIsLink && Directory.Exists(source);
        if (!sourceIsLink && !sourceIsDirectory && !File.Exists(source))
        {
            throw new FileNotFoundException($"File or directory not found: {from}");
        }
        if (source == destination)
        {
            return;
        }
        if (sourceIsDirectory && ContainedPath.IsWithin(source, destination))
        {
            throw new ArgumentException("A directory cannot be moved into itself.");
        }

        PrepareDestination(destination, to, overwrite, allowReplace: !sourceIsDirectory);
        await EnsureDirectoryAsync(serverRoot, Path.GetDirectoryName(destination)!, cancellationToken);

        if (sourceIsDirectory)
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination, overwrite);
        }

        _logger.LogInformation("Moved {From} to {To} for server {ServerName}", from, to, serverName);
    }

    public async Task CopyAsync(string serverName, string from, string to, bool overwrite, CancellationToken cancellationToken)
    {
        var serverRoot = GetServerRoot(serverName);
        // Copy what a link points at (it is checked to be inside the server).
        var source = ResolveTarget(serverName, from);
        var destination = ResolveEntry(serverName, to);
        EnsureNotRoot(serverRoot, destination, "replaced");

        var sourceIsDirectory = Directory.Exists(source);
        if (!sourceIsDirectory && !File.Exists(source))
        {
            throw new FileNotFoundException($"File or directory not found: {from}");
        }
        if (source == destination)
        {
            throw new InvalidOperationException("Source and destination are the same.");
        }
        if (sourceIsDirectory && ContainedPath.IsWithin(source, destination))
        {
            throw new ArgumentException("A directory cannot be copied into itself.");
        }

        PrepareDestination(destination, to, overwrite, allowReplace: !sourceIsDirectory);
        await EnsureDirectoryAsync(serverRoot, Path.GetDirectoryName(destination)!, cancellationToken);

        if (sourceIsDirectory)
        {
            await CopyDirectoryAsync(source, destination, cancellationToken);
        }
        else
        {
            File.Copy(source, destination, overwrite);
            await ChownAsync(destination, cancellationToken);
        }

        _logger.LogInformation("Copied {From} to {To} for server {ServerName}", from, to, serverName);
    }

    private async Task CopyDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        await ChownAsync(destination, cancellationToken);

        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.LinkTarget != null)
            {
                // Following links here could copy files from outside the server in.
                continue;
            }

            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo)
            {
                await CopyDirectoryAsync(entry.FullName, target, cancellationToken);
            }
            else
            {
                File.Copy(entry.FullName, target);
                await ChownAsync(target, cancellationToken);
            }
        }
    }

    public Task<FileDownloadDto> GetDownloadInfoAsync(string serverName, string path, CancellationToken cancellationToken)
    {
        var target = ResolveTarget(serverName, path);

        if (Directory.Exists(target))
        {
            var name = target == GetServerRoot(serverName) ? serverName : Path.GetFileName(target);
            var info = new DirectoryInfo(target);
            return Task.FromResult(new FileDownloadDto($"{name}.zip", true, null, info.LastWriteTimeUtc));
        }
        if (File.Exists(target))
        {
            var info = new FileInfo(target);
            return Task.FromResult(new FileDownloadDto(info.Name, false, info.Length, info.LastWriteTimeUtc));
        }

        throw new FileNotFoundException($"File or directory not found: {path}");
    }

    public Task<Stream> OpenReadAsync(string serverName, string path, CancellationToken cancellationToken)
    {
        var target = ResolveTarget(serverName, path);
        if (!File.Exists(target))
        {
            throw new FileNotFoundException($"File not found: {path}");
        }

        // ReadWrite sharing: a running server may still be writing its logs.
        Stream stream = new FileStream(
            target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        return Task.FromResult(stream);
    }

    public async Task WriteDirectoryZipAsync(string serverName, string path, Stream output, CancellationToken cancellationToken)
    {
        var root = ResolveTarget(serverName, path);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Directory not found: {path}");
        }

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await AddDirectoryToZipAsync(zip, root, string.Empty, cancellationToken);
    }

    private static async Task AddDirectoryToZipAsync(
        ZipArchive zip, string directory, string prefix, CancellationToken cancellationToken)
    {
        var any = false;
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.LinkTarget != null)
            {
                continue;
            }
            any = true;

            var name = prefix + entry.Name;
            if (entry is DirectoryInfo)
            {
                await AddDirectoryToZipAsync(zip, entry.FullName, name + "/", cancellationToken);
                continue;
            }

            FileStream source;
            try
            {
                source = new FileStream(
                    entry.FullName, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
            {
                // Gone or unreadable since it was listed (a running server rotates
                // logs, deletes session files); the rest of the archive is still useful.
                continue;
            }

            await using (source)
            {
                var zipEntry = zip.CreateEntry(name, CompressionLevel.Fastest);
                zipEntry.LastWriteTime = entry.LastWriteTime;
                await using var target = zipEntry.Open();
                await source.CopyToAsync(target, cancellationToken);
            }
        }

        // Keep empty folders: a server may expect them to exist.
        if (!any && prefix.Length > 0)
        {
            zip.CreateEntry(prefix);
        }
    }

    /// <summary>Anything at all at the path, including a dangling link.</summary>
    private static bool Occupied(string fullPath) =>
        File.Exists(fullPath) || Directory.Exists(fullPath) || new FileInfo(fullPath).LinkTarget != null;

    private static void EnsureFree(string fullPath, string displayPath)
    {
        if (Occupied(fullPath))
        {
            throw new InvalidOperationException($"'{displayPath}' already exists.");
        }
    }

    private static void PrepareDestination(string destination, string displayPath, bool overwrite, bool allowReplace)
    {
        if (!Occupied(destination))
        {
            return;
        }
        if (Directory.Exists(destination) && new FileInfo(destination).LinkTarget == null)
        {
            throw new InvalidOperationException($"A directory '{displayPath}' already exists.");
        }
        if (!overwrite || !allowReplace)
        {
            throw new InvalidOperationException($"'{displayPath}' already exists.");
        }
    }

    /// <summary>
    /// Creates <paramref name="directory"/> and chowns every level below the server
    /// root to the server user — also levels that already existed, so a wrongly
    /// owned folder repairs itself (see the filesystem-ownership skill).
    /// </summary>
    private async Task EnsureDirectoryAsync(string serverRoot, string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);

        var levels = new List<string>();
        for (var current = directory;
             current != null && current != serverRoot && ContainedPath.IsWithin(serverRoot, current);
             current = Path.GetDirectoryName(current))
        {
            levels.Add(current);
        }

        foreach (var level in levels)
        {
            await ChownAsync(level, cancellationToken);
        }
    }

    private Task ChownAsync(string path, CancellationToken cancellationToken) =>
        OwnershipHelper.ChangeOwnershipAsync(path, _hostOptions.RunAsUid, _hostOptions.RunAsGid, _logger, cancellationToken);

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove temporary file {Path}", path);
        }
    }
}
