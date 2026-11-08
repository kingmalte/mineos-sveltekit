using MineOS.Application.Dtos;

namespace MineOS.Application.Interfaces;

/// <summary>
/// Finds newer versions of installed mods on Modrinth (matched by file hash, for
/// the server's loader and Minecraft version) and installs them.
/// </summary>
public interface IModUpdateService
{
    /// <exception cref="InvalidOperationException">The loader or Minecraft version is unknown.</exception>
    Task<ModUpdateCheckDto> CheckAsync(string serverName, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces <paramref name="fileName"/> with the newest compatible version. The
    /// target is looked up again on the server - never taken from the client.
    /// </summary>
    Task<ModUpdateResultDto> UpdateAsync(string serverName, string fileName, CancellationToken cancellationToken);
}
