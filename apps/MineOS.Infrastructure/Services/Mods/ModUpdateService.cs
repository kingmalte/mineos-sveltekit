using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MineOS.Application.Dtos;
using MineOS.Application.Interfaces;
using MineOS.Application.Options;
using MineOS.Infrastructure.Utilities;

namespace MineOS.Infrastructure.Services.Mods;

/// <summary>
/// Mod updates via Modrinth, matched by the SHA1 of each installed jar - a file
/// name says nothing reliable about which project or version it is.
/// </summary>
public sealed class ModUpdateService : IModUpdateService
{
    private const string Algorithm = "sha1";

    private readonly HostOptions _hostOptions;
    private readonly IServerService _serverService;
    private readonly IModrinthService _modrinth;
    private readonly IModpackRepository _modpackRepo;
    private readonly ILogger<ModUpdateService> _logger;

    public ModUpdateService(
        IOptions<HostOptions> hostOptions,
        IServerService serverService,
        IModrinthService modrinth,
        IModpackRepository modpackRepo,
        ILogger<ModUpdateService> logger)
    {
        _hostOptions = hostOptions.Value;
        _serverService = serverService;
        _modrinth = modrinth;
        _modpackRepo = modpackRepo;
        _logger = logger;
    }

    public async Task<ModUpdateCheckDto> CheckAsync(string serverName, CancellationToken cancellationToken)
    {
        var (loaders, gameVersion, loader) = await RequireTargetAsync(serverName, cancellationToken);
        var modsPath = GetModsPath(serverName);

        var jars = EnabledJars(modsPath).ToList();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // hash -> file name
        foreach (var (name, path) in jars)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hashes.TryAdd(await HashFileAsync(path, cancellationToken), name);
        }

        if (hashes.Count == 0)
        {
            return new ModUpdateCheckDto(loader, gameVersion, 0, 0, Array.Empty<ModUpdateDto>());
        }

        var hashList = hashes.Keys.ToList();
        var current = await _modrinth.GetVersionsByFileHashesAsync(hashList, Algorithm, cancellationToken);
        var latest = await _modrinth.GetLatestVersionsByFileHashesAsync(
            hashList, Algorithm, loaders, new[] { gameVersion }, cancellationToken);

        var updates = new List<ModUpdateDto>();
        foreach (var (hash, newest) in latest)
        {
            if (!hashes.TryGetValue(hash, out var fileName)
                || !current.TryGetValue(hash, out var installed)
                || !IsNewer(newest, installed)
                || PrimaryJar(newest) is not { } file)
            {
                continue;
            }

            updates.Add(new ModUpdateDto(
                fileName, newest.ProjectId, installed.VersionNumber, newest.Id,
                newest.VersionNumber, file.FileName, newest.DatePublished));
        }

        return new ModUpdateCheckDto(
            loader, gameVersion, jars.Count, current.Count,
            updates.OrderBy(u => u.FileName, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public async Task<ModUpdateResultDto> UpdateAsync(string serverName, string fileName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName
            || !fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid mod file name");
        }

        var (loaders, gameVersion, _) = await RequireTargetAsync(serverName, cancellationToken);
        var modsPath = GetModsPath(serverName);
        var oldPath = Path.Combine(modsPath, fileName);
        if (!File.Exists(oldPath) || new FileInfo(oldPath).LinkTarget != null)
        {
            throw new FileNotFoundException($"Mod '{fileName}' not found");
        }

        // Look the target up again here; nothing about the new file comes from the client.
        var hash = await HashFileAsync(oldPath, cancellationToken);
        var current = await _modrinth.GetVersionsByFileHashesAsync(new[] { hash }, Algorithm, cancellationToken);
        var latest = await _modrinth.GetLatestVersionsByFileHashesAsync(
            new[] { hash }, Algorithm, loaders, new[] { gameVersion }, cancellationToken);

        if (!current.TryGetValue(hash, out var installed) || !latest.TryGetValue(hash, out var newest)
            || !IsNewer(newest, installed))
        {
            throw new InvalidOperationException($"{fileName} is already up to date.");
        }

        var file = PrimaryJar(newest)
            ?? throw new InvalidOperationException($"Modrinth lists no jar for {newest.VersionNumber}.");
        // https only; plain http just for a local stand-in API (Modrinth:BaseUrl in tests).
        var isHttps = Uri.TryCreate(file.Url, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps;
        var isLocal = url != null && url.Scheme == Uri.UriSchemeHttp && url.IsLoopback;
        if (!isHttps && !isLocal)
        {
            throw new InvalidOperationException("Modrinth returned a download that is not https.");
        }

        var newName = file.FileName;
        var newPath = Path.Combine(modsPath, newName);
        if (!string.Equals(newName, fileName, StringComparison.Ordinal)
            && (File.Exists(newPath) || new FileInfo(newPath).LinkTarget != null))
        {
            throw new InvalidOperationException($"{newName} already exists in the mods folder.");
        }

        await using (var download = await _modrinth.OpenDownloadStreamAsync(file.Url, cancellationToken))
        {
            string? downloadedHash = null;
            await AtomicFile.WriteAsync(
                newPath,
                async (target, ct) =>
                {
                    using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await download.ReadAsync(buffer, ct)) > 0)
                    {
                        sha1.AppendData(buffer, 0, read);
                        await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    downloadedHash = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
                },
                async (temp, ct) =>
                {
                    // Never put a corrupt or swapped download in place of a working mod.
                    if (file.Sha1 != null && !string.Equals(file.Sha1, downloadedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"The download of {newName} did not match Modrinth's checksum.");
                    }
                    await OwnershipHelper.ChangeOwnershipAsync(
                        temp, _hostOptions.RunAsUid, _hostOptions.RunAsGid, _logger, ct);
                },
                cancellationToken);
        }

        if (!string.Equals(newName, fileName, StringComparison.Ordinal))
        {
            File.Delete(oldPath);
            try
            {
                await _modpackRepo.RenameModFileAsync(serverName, fileName, newName, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not update the mod record for {FileName}", fileName);
            }
        }

        await _serverService.MarkRestartRequiredAsync(serverName, cancellationToken);
        _logger.LogInformation("Updated {Old} to {New} ({Version}) for server {Server}",
            fileName, newName, newest.VersionNumber, serverName);
        return new ModUpdateResultDto(fileName, newName, newest.VersionNumber);
    }

    private async Task<(IReadOnlyList<string> Loaders, string GameVersion, string Loader)> RequireTargetAsync(
        string serverName, CancellationToken cancellationToken)
    {
        var info = await _serverService.DetectLoaderAsync(serverName, cancellationToken);
        if (info.Loader is not ("fabric" or "quilt" or "forge" or "neoforge"))
        {
            throw new InvalidOperationException(
                "The server's mod loader is unknown. Choose it on the mods page first.");
        }
        if (string.IsNullOrWhiteSpace(info.MinecraftVersion))
        {
            // Without it Modrinth would offer versions for any game version.
            throw new InvalidOperationException(
                "The server's Minecraft version could not be determined, so updates cannot be matched to it.");
        }

        // Quilt runs Fabric mods, so their Fabric builds are valid updates too.
        IReadOnlyList<string> loaders = info.Loader == "quilt" ? new[] { "quilt", "fabric" } : new[] { info.Loader };
        return (loaders, info.MinecraftVersion, info.Loader);
    }

    private string GetModsPath(string serverName)
    {
        var serverPath = Path.Combine(_hostOptions.BaseDirectory, _hostOptions.ServersPathSegment, serverName);
        if (!Directory.Exists(serverPath))
        {
            throw new DirectoryNotFoundException($"Server '{serverName}' not found");
        }
        return ContainedPath.Resolve(serverPath, "mods", followFinal: true);
    }

    private static IEnumerable<(string Name, string Path)> EnabledJars(string modsPath)
    {
        if (!Directory.Exists(modsPath))
        {
            yield break;
        }
        foreach (var file in new DirectoryInfo(modsPath).EnumerateFiles("*.jar"))
        {
            if (file.LinkTarget == null)
            {
                yield return (file.Name, file.FullName);
            }
        }
    }

    private static bool IsNewer(ModrinthVersionDto newest, ModrinthVersionDto installed) =>
        !string.Equals(newest.Id, installed.Id, StringComparison.Ordinal)
        && string.Equals(newest.ProjectId, installed.ProjectId, StringComparison.Ordinal)
        && newest.DatePublished > installed.DatePublished;

    private static ModrinthVersionFileDto? PrimaryJar(ModrinthVersionDto version)
    {
        var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault();
        return file != null
               && file.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
               && Path.GetFileName(file.FileName) == file.FileName
            ? file
            : null;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
