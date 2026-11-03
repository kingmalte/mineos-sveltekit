using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MineOS.Infrastructure.Services.Mods;

/// <summary>A dependency as a mod declares it.</summary>
/// <param name="Required">False for optional/recommended/suggested.</param>
/// <param name="ClientSideOnly">Forge's side="CLIENT": irrelevant on a server.</param>
internal sealed record ModDependency(string ModId, bool Required, bool ClientSideOnly);

/// <summary>What one mod jar declares about itself.</summary>
/// <param name="Platform">fabric, quilt, forge or neoforge.</param>
/// <param name="Environment">both, client or server.</param>
/// <param name="ProvidedIds">Every mod id this jar makes available: its own, "provides" aliases and those of jars nested inside it.</param>
internal sealed record ModMetadata(
    string ModId,
    string? Name,
    string? Version,
    string Platform,
    string Environment,
    IReadOnlyList<string> ProvidedIds,
    IReadOnlyList<ModDependency> Dependencies);

/// <summary>
/// Reads fabric.mod.json, quilt.mod.json and META-INF/(neoforge.)mods.toml from a
/// mod jar, including jars nested inside it (Fabric's "jars", Forge's jarjar).
/// </summary>
/// <remarks>
/// Nested jars matter: Fabric API alone ships ~40 modules, and mods depend on those
/// module ids. Ignoring them would report most Fabric servers as missing
/// dependencies they actually have.
/// </remarks>
internal static class ModMetadataReader
{
    private const long MaxMetadataBytes = 1024 * 1024;
    private const long MaxNestedJarBytes = 64 * 1024 * 1024;
    private const int MaxNestingDepth = 3;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Null when the file is not a readable jar or carries no loader metadata.</summary>
    public static ModMetadata? Read(string jarPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            return Read(archive, depth: 0);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    internal static ModMetadata? Read(ZipArchive archive, int depth)
    {
        return ReadFabric(archive, depth)
            ?? ReadQuilt(archive, depth)
            ?? ReadForge(archive, depth, "META-INF/neoforge.mods.toml", "neoforge")
            ?? ReadForge(archive, depth, "META-INF/mods.toml", "forge");
    }

    // ---- Fabric ------------------------------------------------------------------

    private static ModMetadata? ReadFabric(ZipArchive archive, int depth)
    {
        using var doc = ReadJson(archive, "fabric.mod.json");
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var root = doc.RootElement;
        var id = GetString(root, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var provided = new List<string> { id };
        provided.AddRange(GetStringArray(root, "provides"));

        var dependencies = new List<ModDependency>();
        AddFabricDependencies(root, "depends", required: true, dependencies);
        AddFabricDependencies(root, "recommends", required: false, dependencies);

        if (root.TryGetProperty("jars", out var jars) && jars.ValueKind == JsonValueKind.Array)
        {
            foreach (var jar in jars.EnumerateArray())
            {
                var file = jar.ValueKind == JsonValueKind.Object ? GetString(jar, "file") : null;
                if (file != null)
                {
                    provided.AddRange(NestedProvidedIds(archive, file, depth));
                }
            }
        }

        var environment = GetString(root, "environment") switch
        {
            "client" => "client",
            "server" => "server",
            _ => "both"
        };

        return new ModMetadata(id, GetString(root, "name"), GetString(root, "version"), "fabric",
            environment, provided, dependencies);
    }

    private static void AddFabricDependencies(JsonElement root, string property, bool required, List<ModDependency> into)
    {
        if (!root.TryGetProperty(property, out var deps) || deps.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        foreach (var dep in deps.EnumerateObject())
        {
            into.Add(new ModDependency(dep.Name, required, ClientSideOnly: false));
        }
    }

    // ---- Quilt -------------------------------------------------------------------

    private static ModMetadata? ReadQuilt(ZipArchive archive, int depth)
    {
        using var doc = ReadJson(archive, "quilt.mod.json");
        if (doc == null || !doc.RootElement.TryGetProperty("quilt_loader", out var loader)
            || loader.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetString(loader, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var provided = new List<string> { id };
        if (loader.TryGetProperty("provides", out var provides) && provides.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in provides.EnumerateArray())
            {
                var pid = p.ValueKind == JsonValueKind.String ? p.GetString() : GetString(p, "id");
                if (!string.IsNullOrWhiteSpace(pid)) provided.Add(pid);
            }
        }

        var dependencies = new List<ModDependency>();
        if (loader.TryGetProperty("depends", out var depends) && depends.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in depends.EnumerateArray())
            {
                if (d.ValueKind == JsonValueKind.String)
                {
                    dependencies.Add(new ModDependency(d.GetString()!, true, false));
                }
                else if (d.ValueKind == JsonValueKind.Object && GetString(d, "id") is { } did)
                {
                    var optional = d.TryGetProperty("optional", out var o) && o.ValueKind == JsonValueKind.True;
                    dependencies.Add(new ModDependency(did, !optional, false));
                }
            }
        }

        if (loader.TryGetProperty("jars", out var jars) && jars.ValueKind == JsonValueKind.Array)
        {
            foreach (var jar in jars.EnumerateArray())
            {
                if (jar.ValueKind == JsonValueKind.String)
                {
                    provided.AddRange(NestedProvidedIds(archive, jar.GetString()!, depth));
                }
            }
        }

        var environment = "both";
        if (doc.RootElement.TryGetProperty("minecraft", out var mc) && mc.ValueKind == JsonValueKind.Object)
        {
            environment = GetString(mc, "environment") switch
            {
                "client" => "client",
                "dedicated_server" => "server",
                _ => "both"
            };
        }

        string? name = null;
        if (loader.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            name = GetString(meta, "name");
        }

        return new ModMetadata(id, name, GetString(loader, "version"), "quilt", environment, provided, dependencies);
    }

    // ---- Forge / NeoForge -----------------------------------------------------------

    private static ModMetadata? ReadForge(ZipArchive archive, int depth, string entryName, string platform)
    {
        var text = ReadText(archive, entryName);
        if (text == null)
        {
            return null;
        }

        var toml = MiniToml.Parse(text);
        var mods = toml.ArrayTables("mods");
        if (mods.Count == 0)
        {
            return null;
        }

        var primary = mods[0];
        var id = primary.GetString("modId");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var provided = mods.Select(m => m.GetString("modId")).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m!).ToList();

        var dependencies = new List<ModDependency>();
        foreach (var modId in provided)
        {
            foreach (var dep in toml.ArrayTables($"dependencies.{modId}"))
            {
                var depId = dep.GetString("modId");
                if (string.IsNullOrWhiteSpace(depId))
                {
                    continue;
                }
                // Forge: mandatory=true; NeoForge 20.4+: type="required".
                var type = dep.GetString("type")?.ToLowerInvariant();
                var required = type != null ? type == "required" : dep.GetBool("mandatory") ?? false;
                var side = dep.GetString("side")?.ToUpperInvariant();
                dependencies.Add(new ModDependency(depId, required, side == "CLIENT"));
            }
        }

        // jar-in-jar libraries (META-INF/jarjar/*.jar)
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.StartsWith("META-INF/jarjar/", StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                provided.AddRange(NestedProvidedIds(archive, entry.FullName, depth));
            }
        }

        // Forge 1.20.2+ lets a mod mark itself client-only.
        var environment = toml.Root.GetBool("clientSideOnly") == true ? "client" : "both";

        var version = primary.GetString("version");
        if (version != null && version.Contains("${file.jarVersion}", StringComparison.Ordinal))
        {
            version = ManifestValue(archive, "Implementation-Version") ?? version;
        }

        return new ModMetadata(id, primary.GetString("displayName"), version, platform, environment, provided, dependencies);
    }

    // ---- helpers --------------------------------------------------------------

    private static IEnumerable<string> NestedProvidedIds(ZipArchive archive, string path, int depth)
    {
        if (depth >= MaxNestingDepth)
        {
            return Array.Empty<string>();
        }

        var entry = archive.GetEntry(path.TrimStart('/'));
        if (entry == null || entry.Length > MaxNestedJarBytes)
        {
            return Array.Empty<string>();
        }

        try
        {
            using var buffer = new MemoryStream();
            using (var source = entry.Open())
            {
                source.CopyTo(buffer);
            }
            buffer.Position = 0;
            using var nested = new ZipArchive(buffer, ZipArchiveMode.Read);
            return Read(nested, depth + 1)?.ProvidedIds ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            return Array.Empty<string>();
        }
    }

    private static string? ReadText(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName);
        if (entry == null || entry.Length > MaxMetadataBytes)
        {
            return null;
        }
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static JsonDocument? ReadJson(ZipArchive archive, string entryName)
    {
        var text = ReadText(archive, entryName);
        if (text == null)
        {
            return null;
        }
        try
        {
            return JsonDocument.Parse(text, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ManifestValue(ZipArchive archive, string key)
    {
        var text = ReadText(archive, "META-INF/MANIFEST.MF");
        if (text == null)
        {
            return null;
        }
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(key.Length + 1)..].Trim();
            }
        }
        return null;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IEnumerable<string> GetStringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                yield return item.GetString()!;
            }
        }
    }
}
