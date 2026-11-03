using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MineOS.Infrastructure.Utilities;

/// <summary>
/// Finds a server's Minecraft version from what is on disk, without the server
/// running.
/// </summary>
/// <remarks>
/// The mods page filtered Modrinth/CurseForge results by the version the server
/// reported when pinged - so a stopped server, which is when people install mods,
/// got unfiltered results and incompatible mods. The jar name only helps when it
/// carries the version; "server.jar" and "fabric-server-launch.jar" do not.
/// </remarks>
internal static class MinecraftVersionProbe
{
    private static readonly Regex LeadingVersion = new(@"^(?<v>\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex FabricRemapped = new(@"^minecraft-(?<v>\d+\.\d+(?:\.\d+)?)-", RegexOptions.Compiled);

    public static string? FromDisk(string serverPath, string? configuredJar)
    {
        return FromLibraries(serverPath)
            ?? FromFabricCache(serverPath)
            ?? FromVersionJson(serverPath, configuredJar);
    }

    /// <summary>Forge/NeoForge installers lay out libraries/net/minecraft/server/1.20.1-20230612.114412/.</summary>
    private static string? FromLibraries(string serverPath)
    {
        var dir = Path.Combine(serverPath, "libraries", "net", "minecraft", "server");
        return Newest(dir, name => LeadingVersion.Match(name));
    }

    /// <summary>The Fabric launcher caches .fabric/remappedJars/minecraft-1.20.4-0.15.7/.</summary>
    private static string? FromFabricCache(string serverPath)
    {
        var dir = Path.Combine(serverPath, ".fabric", "remappedJars");
        return Newest(dir, name => FabricRemapped.Match(name));
    }

    private static string? Newest(string directory, Func<string, Match> parse)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        return new DirectoryInfo(directory).EnumerateDirectories()
            .Select(d => (Dir: d, Match: parse(d.Name)))
            .Where(x => x.Match.Success)
            .OrderByDescending(x => x.Dir.LastWriteTimeUtc)
            .Select(x => x.Match.Groups["v"].Value)
            .FirstOrDefault();
    }

    /// <summary>
    /// Vanilla server jars (and the 1.18+ bundler jars) carry version.json with the
    /// game version in "id". Fabric/Quilt launchers start such a jar, usually
    /// server.jar or whatever fabric-server-launcher.properties names.
    /// </summary>
    private static string? FromVersionJson(string serverPath, string? configuredJar)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredJar)
            && configuredJar.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            && !configuredJar.StartsWith('@'))
        {
            candidates.Add(configuredJar);
        }

        var launcherProps = Path.Combine(serverPath, "fabric-server-launcher.properties");
        if (File.Exists(launcherProps))
        {
            foreach (var line in File.ReadLines(launcherProps))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("serverJar=", StringComparison.Ordinal))
                {
                    candidates.Add(trimmed["serverJar=".Length..].Trim());
                }
            }
        }
        candidates.Add("server.jar");

        foreach (var candidate in candidates.Distinct())
        {
            string fullPath;
            try
            {
                // Only jars inside the server directory, links resolved.
                fullPath = ContainedPath.Resolve(serverPath, candidate, followFinal: true);
            }
            catch (ArgumentException)
            {
                continue;
            }

            var version = ReadVersionJson(fullPath);
            if (version != null)
            {
                return version;
            }
        }

        return null;
    }

    internal static string? ReadVersionJson(string jarPath)
    {
        if (!File.Exists(jarPath))
        {
            return null;
        }

        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            var entry = archive.GetEntry("version.json");
            if (entry == null || entry.Length > 64 * 1024)
            {
                return null;
            }

            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
