using MineOS.Application.Dtos;

namespace MineOS.Application.Interfaces;

public interface IModService
{
    Task<IReadOnlyList<InstalledModDto>> ListModsAsync(string serverName, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstalledModWithModpackDto>> ListModsWithModpacksAsync(string serverName, CancellationToken cancellationToken);
    Task SaveModAsync(string serverName, string fileName, Stream content, CancellationToken cancellationToken);

    /// <summary>Like <see cref="SaveModAsync"/>, returning the jar files written.</summary>
    Task<IReadOnlyList<string>> SaveModFilesAsync(string serverName, string fileName, Stream content, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes several mods in one call. Every name is validated before anything is
    /// deleted; files that cannot be deleted are reported, not thrown.
    /// </summary>
    Task<ModDeleteBatchResultDto> DeleteModsAsync(string serverName, IReadOnlyList<string> fileNames, CancellationToken cancellationToken);

    /// <summary>Moves a mod from the server's mods folder to its client-only mods.</summary>
    Task MoveModToClientAsync(string serverName, string fileName, CancellationToken cancellationToken);
    Task DeleteModAsync(string serverName, string fileName, CancellationToken cancellationToken);
    Task<string> GetModPathAsync(string serverName, string fileName, CancellationToken cancellationToken);
    Task InstallModFromCurseForgeAsync(
        string serverName,
        int modId,
        int? fileId,
        IProgress<JobProgressDto> progress,
        CancellationToken cancellationToken);
    Task InstallModpackAsync(
        string serverName,
        int modpackId,
        int? fileId,
        IProgress<JobProgressDto> progress,
        CancellationToken cancellationToken);

    Task InstallModrinthModpackAsync(
        string serverName,
        string projectId,
        string versionId,
        string? projectName,
        string? projectVersion,
        string? logoUrl,
        IProgress<JobProgressDto> progress,
        CancellationToken cancellationToken);

    // Enhanced modpack methods with state tracking and rollback
    Task InstallModpackWithStateAsync(
        string serverName,
        int modpackId,
        int? fileId,
        string modpackName,
        string? modpackVersion,
        string? logoUrl,
        IModpackInstallState state,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<InstalledModpackDto>> ListInstalledModpacksAsync(string serverName, CancellationToken cancellationToken);
    Task UninstallModpackAsync(string serverName, int modpackDbId, CancellationToken cancellationToken);

    Task<string> SetModEnabledAsync(string serverName, string filename, bool enabled, CancellationToken cancellationToken);

    // Client-only mods (stored in client-mods/mods/, included in client packages only)
    Task<IReadOnlyList<InstalledModWithModpackDto>> ListClientModsAsync(string serverName, CancellationToken cancellationToken);
    Task SaveClientModAsync(string serverName, string fileName, Stream content, CancellationToken cancellationToken);
    Task DeleteClientModAsync(string serverName, string fileName, CancellationToken cancellationToken);
    Task<string> GetClientModPathAsync(string serverName, string fileName, CancellationToken cancellationToken);
}
