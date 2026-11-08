using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MineOS.Application.Dtos;
using MineOS.Application.Interfaces;

namespace MineOS.Api.Endpoints;

public static class FileEndpoints
{
    public static RouteGroupBuilder MapFileEndpoints(this RouteGroupBuilder servers)
    {
        servers.MapGet("/{name}/files", async (
            string name,
            [FromQuery] bool? download,
            HttpContext http,
            IFileService fileService,
            CancellationToken cancellationToken) =>
        {
            return download == true
                ? await HandleDownloadAsync(name, "/", http, fileService, cancellationToken)
                : await HandleFileBrowseAsync(name, "/", fileService, cancellationToken);
        });

        servers.MapGet("/{name}/files/{*path}", async (
            string name,
            string path,
            [FromQuery] bool? download,
            HttpContext http,
            IFileService fileService,
            CancellationToken cancellationToken) =>
        {
            var normalizedPath = NormalizePath(path);
            return download == true
                ? await HandleDownloadAsync(name, normalizedPath, http, fileService, cancellationToken)
                : await HandleFileBrowseAsync(name, normalizedPath, fileService, cancellationToken);
        });

        // Upload file content
        servers.MapPost("/{name}/files/{*path}", async (
            string name,
            string path,
            HttpRequest request,
            IFileService fileService,
            CancellationToken cancellationToken) =>
        {
            var normalizedPath = NormalizePath(path);

            try
            {
                if (request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var payload = await JsonSerializer.DeserializeAsync<FileUploadRequest>(
                        request.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                        cancellationToken);

                    if (payload == null || (string.IsNullOrWhiteSpace(payload.Content) && string.IsNullOrWhiteSpace(payload.ContentBase64)))
                    {
                        return Results.BadRequest(new { error = "File content is required" });
                    }

                    if (!string.IsNullOrWhiteSpace(payload.ContentBase64))
                    {
                        var bytes = Convert.FromBase64String(payload.ContentBase64);
                        await fileService.WriteFileBytesAsync(name, normalizedPath, bytes, cancellationToken);
                    }
                    else
                    {
                        await fileService.WriteFileAsync(name, normalizedPath, payload.Content ?? string.Empty, cancellationToken);
                    }
                }
                else
                {
                    // Streamed straight to disk: buffering a multi-GB world upload in
                    // memory took the API down (the body size is not capped).
                    await fileService.WriteFileStreamAsync(name, normalizedPath, request.Body, cancellationToken);
                }

                return Results.Ok(new { message = $"File '{normalizedPath}' uploaded" });
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(new { error = $"Invalid base64 content: {ex.Message}" });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // Update file content (text)
        servers.MapPut("/{name}/files/{*path}", async (
            string name,
            string path,
            HttpRequest request,
            IFileService fileService,
            CancellationToken cancellationToken) =>
        {
            var normalizedPath = NormalizePath(path);

            try
            {
                string? content = null;

                if (request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var payload = await JsonSerializer.DeserializeAsync<FileUpdateRequest>(
                        request.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                        cancellationToken);
                    content = payload?.Content;
                }
                else
                {
                    using var reader = new StreamReader(request.Body);
                    content = await reader.ReadToEndAsync(cancellationToken);
                }

                if (content == null)
                {
                    return Results.BadRequest(new { error = "File content is required" });
                }

                await fileService.WriteFileAsync(name, normalizedPath, content, cancellationToken);
                return Results.Ok(new { message = $"File '{normalizedPath}' updated" });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // Delete file or directory
        servers.MapDelete("/{name}/files/{*path}", async (
            string name,
            string path,
            IFileService fileService,
            CancellationToken cancellationToken) =>
        {
            var normalizedPath = NormalizePath(path);
            try
            {
                await fileService.DeleteFileAsync(name, normalizedPath, cancellationToken);
                return Results.Ok(new { message = $"Deleted '{normalizedPath}'" });
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // New file / new folder / move (rename) / copy. POSTs, so ServerAccessFilter
        // requires Control - the same as uploading, editing and deleting.
        servers.MapPost("/{name}/file-actions/create-file", (
                string name, FilePathRequest request, IFileService fileService, CancellationToken ct) =>
            RunFileActionAsync(request.Path, () => fileService.CreateFileAsync(name, NormalizePath(request.Path), ct)));

        servers.MapPost("/{name}/file-actions/create-folder", (
                string name, FilePathRequest request, IFileService fileService, CancellationToken ct) =>
            RunFileActionAsync(request.Path, () => fileService.CreateDirectoryAsync(name, NormalizePath(request.Path), ct)));

        servers.MapPost("/{name}/file-actions/move", (
                string name, FileTransferRequest request, IFileService fileService, CancellationToken ct) =>
            RunFileActionAsync(request.From, () => string.IsNullOrEmpty(request.To)
                ? throw new ArgumentException("A destination path is required")
                : fileService.MoveAsync(name, NormalizePath(request.From), NormalizePath(request.To), request.Overwrite, ct)));

        servers.MapPost("/{name}/file-actions/copy", (
                string name, FileTransferRequest request, IFileService fileService, CancellationToken ct) =>
            RunFileActionAsync(request.From, () => string.IsNullOrEmpty(request.To)
                ? throw new ArgumentException("A destination path is required")
                : fileService.CopyAsync(name, NormalizePath(request.From), NormalizePath(request.To), request.Overwrite, ct)));

        return servers;
    }

    /// <summary>
    /// Makes a path absolute within the server. Deliberately no trimming: " " is a
    /// (strange but legal) file name, and trimming it to "" turned it into "/" - the
    /// server directory itself - which a DELETE then removed wholesale.
    /// </summary>
    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        return path.StartsWith('/') ? path : $"/{path}";
    }

    private static async Task<IResult> RunFileActionAsync(string? path, Func<Task> action)
    {
        if (string.IsNullOrEmpty(path))
        {
            return Results.BadRequest(new { error = "A path is required" });
        }

        try
        {
            await action();
            return Results.Ok(new { message = "Done" });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    /// <summary>A file as itself, a directory as a ZIP streamed while it is built.</summary>
    private static async Task<IResult> HandleDownloadAsync(
        string name,
        string path,
        HttpContext http,
        IFileService fileService,
        CancellationToken cancellationToken)
    {
        FileDownloadDto info;
        try
        {
            info = await fileService.GetDownloadInfoAsync(name, path, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Results.NotFound(new { error = ex.Message });
        }

        if (!info.IsDirectory)
        {
            var stream = await fileService.OpenReadAsync(name, path, cancellationToken);
            return Results.File(stream, "application/octet-stream", info.FileName,
                lastModified: info.Modified, enableRangeProcessing: true);
        }

        // ZipArchive writes its headers synchronously; Kestrel refuses synchronous
        // writes unless the request opts in.
        var bodyControl = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
        if (bodyControl != null)
        {
            bodyControl.AllowSynchronousIO = true;
        }

        return Results.Stream(
            output => fileService.WriteDirectoryZipAsync(name, path, output, cancellationToken),
            "application/zip",
            info.FileName);
    }

    private static async Task<IResult> HandleFileBrowseAsync(
        string name,
        string path,
        IFileService fileService,
        CancellationToken cancellationToken)
    {
        try
        {
            var files = await fileService.ListFilesAsync(name, path, cancellationToken);
            return Results.Ok(new FileBrowseResultDto(path, "directory", files, null));
        }
        catch (DirectoryNotFoundException)
        {
            try
            {
                var file = await fileService.ReadFileAsync(name, path, cancellationToken);
                return Results.Ok(new FileBrowseResultDto(path, "file", null, file));
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}

public record FileUploadRequest(string? Content, string? ContentBase64);
public record FilePathRequest(string? Path);
public record FileTransferRequest(string? From, string? To, bool Overwrite = false);
public record FileUpdateRequest(string Content);
