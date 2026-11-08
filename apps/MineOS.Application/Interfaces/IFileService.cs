using MineOS.Application.Dtos;

namespace MineOS.Application.Interfaces;

/// <summary>
/// File access inside one server's directory, for the web file manager.
/// </summary>
/// <remarks>
/// Every path is relative to the server directory and must stay inside it, also
/// after symbolic links are followed: a path that leads outside is refused with an
/// <see cref="ArgumentException"/>. Missing paths throw
/// <see cref="FileNotFoundException"/> / <see cref="DirectoryNotFoundException"/>;
/// a target that already exists (or another conflict) throws
/// <see cref="InvalidOperationException"/>.
/// </remarks>
public interface IFileService
{
    Task<IReadOnlyList<FileEntryDto>> ListFilesAsync(string serverName, string path, CancellationToken cancellationToken);
    Task<FileContentDto> ReadFileAsync(string serverName, string path, CancellationToken cancellationToken);
    Task<byte[]> ReadFileBytesAsync(string serverName, string path, CancellationToken cancellationToken);
    Task WriteFileAsync(string serverName, string path, string content, CancellationToken cancellationToken);
    Task WriteFileBytesAsync(string serverName, string path, byte[] content, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a file from a stream without holding it in memory. Like every write,
    /// it replaces the file atomically: readers see the old or the new file, never
    /// a half-written one.
    /// </summary>
    Task WriteFileStreamAsync(string serverName, string path, Stream content, CancellationToken cancellationToken);

    Task DeleteFileAsync(string serverName, string path, CancellationToken cancellationToken);

    /// <summary>Creates an empty file; fails if anything already exists at the path.</summary>
    Task CreateFileAsync(string serverName, string path, CancellationToken cancellationToken);

    /// <summary>Creates a directory (and missing parents); fails if anything already exists at the path.</summary>
    Task CreateDirectoryAsync(string serverName, string path, CancellationToken cancellationToken);

    /// <summary>
    /// Moves or renames a file or directory to <paramref name="to"/> (the full new
    /// path). An existing file at the destination is only replaced when
    /// <paramref name="overwrite"/> is set; an existing directory never is.
    /// </summary>
    Task MoveAsync(string serverName, string from, string to, bool overwrite, CancellationToken cancellationToken);

    /// <summary>
    /// Copies a file or a directory tree to <paramref name="to"/> (the full new path).
    /// Symbolic links inside a copied directory are skipped.
    /// </summary>
    Task CopyAsync(string serverName, string from, string to, bool overwrite, CancellationToken cancellationToken);

    /// <summary>Describes what a download of <paramref name="path"/> will deliver.</summary>
    Task<FileDownloadDto> GetDownloadInfoAsync(string serverName, string path, CancellationToken cancellationToken);

    /// <summary>Opens a file for streaming it to the client. The caller disposes the stream.</summary>
    Task<Stream> OpenReadAsync(string serverName, string path, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a directory as a ZIP archive to <paramref name="output"/>. Symbolic links
    /// are skipped; files that vanish while zipping (a running server) are left out.
    /// </summary>
    Task WriteDirectoryZipAsync(string serverName, string path, Stream output, CancellationToken cancellationToken);
}
