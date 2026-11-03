using System.IO.Compression;
using System.Text;
using MineOS.Infrastructure.Services.Mods;

namespace MineOS.Tests.Unit;

/// <summary>Builds small mod jars in memory or on disk for the analysis tests.</summary>
internal static class TestJars
{
    public static byte[] Build(params (string Name, object Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                var bytes = content as byte[] ?? Encoding.UTF8.GetBytes((string)content);
                stream.Write(bytes);
            }
        }
        return buffer.ToArray();
    }

    public static string Fabric(string id, string environment = "*", string depends = "{}", string extra = "") =>
        $$"""{ "schemaVersion": 1, "id": "{{id}}", "version": "1.0.0", "name": "{{id}} mod", "environment": "{{environment}}", "depends": {{depends}} {{extra}} }""";

    public static ModMetadata? Read(byte[] jar)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mineos-jar-{Guid.NewGuid():N}.jar");
        File.WriteAllBytes(path, jar);
        try
        {
            return ModMetadataReader.Read(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class ModMetadataReaderTests
{
    [Fact]
    public void Reads_Fabric_Metadata_Including_Nested_Jars()
    {
        var module = TestJars.Build(("fabric.mod.json", TestJars.Fabric("fabric-lifecycle-events-v1")));
        var fabricApi = TestJars.Build(
            ("fabric.mod.json", TestJars.Fabric("fabric-api", extra: """, "provides": ["fabric"], "jars": [{"file": "META-INF/jars/lifecycle.jar"}]""")),
            ("META-INF/jars/lifecycle.jar", module));

        var meta = TestJars.Read(fabricApi)!;

        Assert.Equal("fabric-api", meta.ModId);
        Assert.Equal("fabric", meta.Platform);
        Assert.Equal("both", meta.Environment);
        Assert.Contains("fabric", meta.ProvidedIds);
        Assert.Contains("fabric-lifecycle-events-v1", meta.ProvidedIds);
    }

    [Fact]
    public void Reads_Fabric_Client_Environment_And_Dependencies()
    {
        var jar = TestJars.Build(("fabric.mod.json",
            TestJars.Fabric("sodium", "client", """{ "fabricloader": ">=0.15", "fabric-api": "*" }""")));

        var meta = TestJars.Read(jar)!;

        Assert.Equal("client", meta.Environment);
        Assert.Contains(meta.Dependencies, d => d.ModId == "fabric-api" && d.Required);
    }

    [Fact]
    public void Reads_Quilt_Metadata()
    {
        var jar = TestJars.Build(("quilt.mod.json", """
            {
              "schema_version": 1,
              "quilt_loader": {
                "id": "qmod", "version": "2.0", "metadata": { "name": "Q Mod" },
                "depends": [ "quilted_fabric_api", { "id": "optional_thing", "optional": true } ],
                "provides": [ "qmod_alias" ]
              },
              "minecraft": { "environment": "dedicated_server" }
            }
            """));

        var meta = TestJars.Read(jar)!;

        Assert.Equal("quilt", meta.Platform);
        Assert.Equal("server", meta.Environment);
        Assert.Equal("Q Mod", meta.Name);
        Assert.Contains("qmod_alias", meta.ProvidedIds);
        Assert.Contains(meta.Dependencies, d => d.ModId == "quilted_fabric_api" && d.Required);
        Assert.Contains(meta.Dependencies, d => d.ModId == "optional_thing" && !d.Required);
    }

    [Fact]
    public void Reads_Forge_Mods_Toml_With_Multiline_Description()
    {
        var jar = TestJars.Build(
            ("META-INF/mods.toml", """
                modLoader="javafml" # the loader
                loaderVersion="[47,)"
                license="MIT"
                [[mods]]
                modId="create"
                version="${file.jarVersion}"
                displayName="Create"
                description='''
                A mod about [[gears]] and = signs.
                [[dependencies.fake]]
                '''
                [[dependencies.create]]
                    modId="forge"
                    mandatory=true
                    versionRange="[47,)"
                    side="BOTH"
                [[dependencies.create]]
                    modId="flywheel"
                    mandatory=true
                    side="BOTH"
                [[dependencies.create]]
                    modId="jei"
                    mandatory=false
                    side="BOTH"
                [[dependencies.create]]
                    modId="ponder_client"
                    mandatory=true
                    side="CLIENT"
                """),
            ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\nImplementation-Version: 0.5.1.f\r\n"));

        var meta = TestJars.Read(jar)!;

        Assert.Equal("create", meta.ModId);
        Assert.Equal("forge", meta.Platform);
        Assert.Equal("Create", meta.Name);
        Assert.Equal("0.5.1.f", meta.Version);
        Assert.DoesNotContain(meta.Dependencies, d => d.ModId == "fake");
        Assert.Contains(meta.Dependencies, d => d.ModId == "flywheel" && d.Required && !d.ClientSideOnly);
        Assert.Contains(meta.Dependencies, d => d.ModId == "jei" && !d.Required);
        Assert.Contains(meta.Dependencies, d => d.ModId == "ponder_client" && d.ClientSideOnly);
    }

    [Fact]
    public void Reads_NeoForge_Type_Field_And_Client_Only_Flag()
    {
        var jar = TestJars.Build(("META-INF/neoforge.mods.toml", """
            modLoader="javafml"
            clientSideOnly=true
            [[mods]]
            modId="zoomify"
            version="2.0"
            [[dependencies.zoomify]]
            modId="yacl"
            type="required"
            side="BOTH"
            """));

        var meta = TestJars.Read(jar)!;

        Assert.Equal("neoforge", meta.Platform);
        Assert.Equal("client", meta.Environment);
        Assert.Contains(meta.Dependencies, d => d.ModId == "yacl" && d.Required);
    }

    [Fact]
    public void A_Plain_Library_Jar_Has_No_Metadata()
    {
        Assert.Null(TestJars.Read(TestJars.Build(("com/example/Lib.class", "x"))));
        Assert.Null(TestJars.Read(Encoding.UTF8.GetBytes("not a zip at all")));
    }
}

public class ModCompatibilityRulesTests
{
    private static ModJar Jar(string file, string id, string platform = "fabric", string env = "both",
        string[]? provides = null, ModDependency[]? deps = null, bool disabled = false) =>
        new(file, disabled, new ModMetadata(id, id, "1.0", platform, env,
            new[] { id }.Concat(provides ?? Array.Empty<string>()).ToList(),
            deps ?? Array.Empty<ModDependency>()));

    private static ModDependency Needs(string id, bool required = true, bool clientSide = false) => new(id, required, clientSide);

    [Fact]
    public void Healthy_Fabric_Setup_Has_No_Issues()
    {
        var issues = ModCompatibilityRules.Check("fabric", new[]
        {
            Jar("fabric-api.jar", "fabric-api", provides: new[] { "fabric", "fabric-lifecycle-events-v1" }),
            Jar("lithium.jar", "lithium", deps: new[] { Needs("fabricloader"), Needs("minecraft"), Needs("fabric-lifecycle-events-v1") }),
            new ModJar("some-library.jar", false, null)
        });

        Assert.Empty(issues);
    }

    [Fact]
    public void Reports_Client_Only_Mods()
    {
        var issue = Assert.Single(ModCompatibilityRules.Check("fabric", new[] { Jar("sodium.jar", "sodium", env: "client") }));
        Assert.Equal("client-only", issue.Kind);
        Assert.Equal(new[] { "sodium.jar" }, issue.FileNames);
    }

    [Fact]
    public void Reports_Missing_And_Disabled_Dependencies()
    {
        var issues = ModCompatibilityRules.Check("fabric", new[]
        {
            Jar("a.jar", "a", deps: new[] { Needs("fabric-api"), Needs("cloth-config"), Needs("optional-only", required: false) }),
            Jar("b.jar", "b", deps: new[] { Needs("cloth-config") }),
            Jar("fabric-api.jar.disabled", "fabric-api", disabled: true)
        });

        Assert.Equal(2, issues.Count);
        var cloth = issues.Single(i => i.Message.Contains("cloth-config"));
        Assert.Equal("missing-dependency", cloth.Kind);
        Assert.Equal(new[] { "a.jar", "b.jar" }, cloth.FileNames);
        Assert.Contains("disabled", issues.Single(i => i.Message.Contains("'fabric-api'")).Message);
    }

    [Fact]
    public void Ignores_Client_Side_Forge_Dependencies()
    {
        var issues = ModCompatibilityRules.Check("forge", new[]
        {
            Jar("create.jar", "create", "forge", deps: new[] { Needs("forge"), Needs("ponder_client", clientSide: true) })
        });

        Assert.Empty(issues);
    }

    [Fact]
    public void Reports_Duplicates()
    {
        var issue = Assert.Single(ModCompatibilityRules.Check("fabric", new[]
        {
            Jar("jei-1.jar", "jei"), Jar("jei-2.jar", "jei"), Jar("jei-old.jar.disabled", "jei", disabled: true)
        }));
        Assert.Equal("duplicate", issue.Kind);
        Assert.Equal(new[] { "jei-1.jar", "jei-2.jar" }, issue.FileNames);
    }

    [Theory]
    [InlineData("forge", "fabric", true)]
    [InlineData("neoforge", "fabric", true)]
    [InlineData("fabric", "forge", true)]
    [InlineData("forge", "neoforge", true)]
    [InlineData("quilt", "fabric", false)]
    [InlineData("neoforge", "forge", false)]
    public void Reports_Mods_For_Another_Loader(string serverLoader, string modPlatform, bool flagged)
    {
        var issues = ModCompatibilityRules.Check(serverLoader, new[] { Jar("x.jar", "x", modPlatform) });

        Assert.Equal(flagged, issues.Any(i => i.Kind == "wrong-loader"));
    }

    [Fact]
    public void Sinytra_Connector_Makes_Fabric_Mods_Fine_On_Forge()
    {
        var issues = ModCompatibilityRules.Check("forge", new[]
        {
            Jar("connector.jar", "connector", "forge"),
            Jar("fabric-mod.jar", "fm", "fabric")
        });

        Assert.DoesNotContain(issues, i => i.Kind == "wrong-loader");
    }
}
