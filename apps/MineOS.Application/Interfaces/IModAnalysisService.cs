using MineOS.Application.Dtos;

namespace MineOS.Application.Interfaces;

/// <summary>
/// Reads the metadata inside a server's mod jars (fabric.mod.json, quilt.mod.json,
/// mods.toml) to catch problems before the server crashes on them.
/// </summary>
public interface IModAnalysisService
{
    /// <summary>
    /// Client-only mods, missing required dependencies, the same mod twice, and mods
    /// for another loader - for the enabled jars in the server's mods folder.
    /// </summary>
    Task<ModCheckResultDto> CheckAsync(string serverName, CancellationToken cancellationToken);

    /// <summary>Which of <paramref name="fileNames"/> (in the mods folder) are client-only.</summary>
    Task<IReadOnlyList<string>> FindClientOnlyAsync(
        string serverName, IReadOnlyList<string> fileNames, CancellationToken cancellationToken);
}
