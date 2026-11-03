using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MineOS.Application.Dtos;
using MineOS.Application.Interfaces;
using MineOS.Application.Options;
using MineOS.Infrastructure.Utilities;

namespace MineOS.Infrastructure.Services.Mods;

public sealed class ModAnalysisService : IModAnalysisService
{
    private readonly HostOptions _hostOptions;
    private readonly IServerService _serverService;
    private readonly ILogger<ModAnalysisService> _logger;

    public ModAnalysisService(
        IOptions<HostOptions> hostOptions,
        IServerService serverService,
        ILogger<ModAnalysisService> logger)
    {
        _hostOptions = hostOptions.Value;
        _serverService = serverService;
        _logger = logger;
    }

    public async Task<ModCheckResultDto> CheckAsync(string serverName, CancellationToken cancellationToken)
    {
        var loader = await _serverService.DetectLoaderAsync(serverName, cancellationToken);
        var jars = ReadJars(serverName, fileNames: null, cancellationToken);

        var mods = jars
            .Select(j => new ModInfoDto(
                j.FileName,
                j.IsDisabled,
                j.Metadata?.ModId,
                j.Metadata?.Name,
                j.Metadata?.Version,
                j.Metadata?.Platform,
                j.Metadata?.Environment ?? "both"))
            .ToList();

        var issues = ModCompatibilityRules.Check(loader.Loader, jars);
        return new ModCheckResultDto(loader.Loader, loader.MinecraftVersion, mods, issues);
    }

    public Task<IReadOnlyList<string>> FindClientOnlyAsync(
        string serverName, IReadOnlyList<string> fileNames, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> clientOnly = ReadJars(serverName, fileNames, cancellationToken)
            .Where(j => j.Metadata?.Environment == "client")
            .Select(j => j.FileName)
            .ToList();
        return Task.FromResult(clientOnly);
    }

    private List<ModJar> ReadJars(string serverName, IReadOnlyList<string>? fileNames, CancellationToken cancellationToken)
    {
        var serverPath = Path.Combine(_hostOptions.BaseDirectory, _hostOptions.ServersPathSegment, serverName);
        if (!Directory.Exists(serverPath))
        {
            throw new DirectoryNotFoundException($"Server '{serverName}' not found");
        }

        var modsPath = ContainedPath.Resolve(serverPath, "mods", followFinal: true);
        if (!Directory.Exists(modsPath))
        {
            return new List<ModJar>();
        }

        var wanted = fileNames == null ? null : new HashSet<string>(fileNames, StringComparer.Ordinal);
        var jars = new List<ModJar>();

        foreach (var file in new DirectoryInfo(modsPath).EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = file.Name;
            var disabled = name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);
            if (!disabled && !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (wanted != null && !wanted.Contains(name))
            {
                continue;
            }

            string realPath;
            try
            {
                // Never open a jar that a link leads outside the mods folder.
                realPath = ContainedPath.Resolve(modsPath, name, followFinal: true);
            }
            catch (ArgumentException)
            {
                _logger.LogWarning("Skipping {FileName}: it links outside the mods folder", name);
                continue;
            }

            jars.Add(new ModJar(name, disabled, ModMetadataReader.Read(realPath)));
        }

        return jars.OrderBy(j => j.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
