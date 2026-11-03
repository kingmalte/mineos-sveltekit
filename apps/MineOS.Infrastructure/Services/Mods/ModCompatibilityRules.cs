using MineOS.Application.Dtos;

namespace MineOS.Infrastructure.Services.Mods;

/// <summary>A jar in the mods folder and what could be read from it.</summary>
internal sealed record ModJar(string FileName, bool IsDisabled, ModMetadata? Metadata);

/// <summary>
/// Turns the metadata of a server's mod jars into problems a person should look at
/// before (re)starting the server. Pure: no I/O, so it is tested directly.
/// </summary>
/// <remarks>
/// Deliberately conservative - every rule reports only what the jars state outright:
/// version ranges are not evaluated, and jars without loader metadata (plain
/// libraries) are ignored rather than guessed at.
/// </remarks>
internal static class ModCompatibilityRules
{
    /// <summary>Ids the loader itself provides; nothing in the mods folder has to.</summary>
    private static readonly Dictionary<string, string[]> BuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fabric"] = new[] { "minecraft", "java", "fabricloader", "fabric-loader", "mixinextras" },
        ["quilt"] = new[] { "minecraft", "java", "fabricloader", "fabric-loader", "quilt_loader", "mixinextras" },
        ["forge"] = new[] { "minecraft", "forge", "javafml", "lowcodefml", "mcp", "mixinextras" },
        ["neoforge"] = new[] { "minecraft", "neoforge", "forge", "javafml", "lowcodefml", "fml", "mixinextras" },
    };

    /// <summary>Lets Forge/NeoForge load Fabric mods; with it, "wrong loader" is not wrong.</summary>
    private const string SinytraConnector = "connector";

    public static IReadOnlyList<ModIssueDto> Check(string? serverLoader, IReadOnlyList<ModJar> jars)
    {
        var issues = new List<ModIssueDto>();
        var enabled = jars.Where(j => !j.IsDisabled && j.Metadata != null).ToList();
        var loader = serverLoader?.ToLowerInvariant();

        // ---- client-only mods -------------------------------------------------------
        foreach (var jar in enabled.Where(j => j.Metadata!.Environment == "client"))
        {
            issues.Add(new ModIssueDto("error", "client-only",
                $"{Label(jar)} is a client-only mod. Dedicated servers usually crash on start with it - move it to the client mods instead.",
                new[] { jar.FileName }));
        }

        // ---- the same mod twice ------------------------------------------------------
        foreach (var group in enabled.GroupBy(j => j.Metadata!.ModId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            issues.Add(new ModIssueDto("error", "duplicate",
                $"The mod '{group.Key}' is installed {group.Count()} times. The loader refuses duplicate mod ids - keep one.",
                group.Select(j => j.FileName).ToList()));
        }

        // ---- mods for another loader -------------------------------------------------
        var provided = new HashSet<string>(
            enabled.SelectMany(j => j.Metadata!.ProvidedIds), StringComparer.OrdinalIgnoreCase);
        if (loader != null && BuiltIns.ContainsKey(loader) && !provided.Contains(SinytraConnector))
        {
            foreach (var jar in enabled.Where(j => !RunsOn(j.Metadata!.Platform, loader)))
            {
                issues.Add(new ModIssueDto("error", "wrong-loader",
                    $"{Label(jar)} is a {Display(jar.Metadata!.Platform)} mod, but this server runs {Display(loader)}.",
                    new[] { jar.FileName }));
            }
        }

        // ---- missing required dependencies ---------------------------------------------
        if (loader != null && BuiltIns.TryGetValue(loader, out var builtIns))
        {
            provided.UnionWith(builtIns);
        }
        else
        {
            // Unknown loader: still accept every loader's built-ins rather than
            // flagging "minecraft" as missing.
            provided.UnionWith(BuiltIns.Values.SelectMany(v => v));
        }

        var disabledProviders = jars.Where(j => j.IsDisabled && j.Metadata != null)
            .SelectMany(j => j.Metadata!.ProvidedIds.Select(id => (Id: id, j.FileName)))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().FileName, StringComparer.OrdinalIgnoreCase);

        var missing = enabled
            .SelectMany(j => j.Metadata!.Dependencies
                .Where(d => d.Required && !d.ClientSideOnly && !provided.Contains(d.ModId))
                .Select(d => (Dependency: d.ModId, Jar: j)))
            .GroupBy(x => x.Dependency, StringComparer.OrdinalIgnoreCase);

        foreach (var group in missing)
        {
            var needers = group.Select(x => x.Jar).ToList();
            var who = string.Join(", ", needers.Select(Label));
            var message = disabledProviders.TryGetValue(group.Key, out var disabledFile)
                ? $"{who} need{(needers.Count == 1 ? "s" : "")} '{group.Key}', which is disabled ({disabledFile})."
                : $"{who} need{(needers.Count == 1 ? "s" : "")} '{group.Key}', which is not installed.";
            issues.Add(new ModIssueDto("error", "missing-dependency", message,
                needers.Select(j => j.FileName).ToList()));
        }

        return issues;
    }

    /// <summary>Quilt loads Fabric mods; NeoForge (still) loads many Forge mods, so that is not flagged.</summary>
    private static bool RunsOn(string platform, string loader) => (platform, loader) switch
    {
        ("fabric", "fabric") or ("fabric", "quilt") or ("quilt", "quilt") => true,
        ("forge", "forge") or ("neoforge", "neoforge") or ("forge", "neoforge") => true,
        _ => false
    };

    private static string Label(ModJar jar) =>
        jar.Metadata?.Name is { Length: > 0 } name ? $"{name} ({jar.FileName})" : jar.FileName;

    private static string Display(string platform) => platform switch
    {
        "neoforge" => "NeoForge",
        "forge" => "Forge",
        "fabric" => "Fabric",
        "quilt" => "Quilt",
        _ => platform
    };
}
