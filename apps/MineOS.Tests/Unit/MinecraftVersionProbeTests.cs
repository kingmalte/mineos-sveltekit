using System.IO.Compression;
using MineOS.Infrastructure.Utilities;

namespace MineOS.Tests.Unit;

public sealed class MinecraftVersionProbeTests : IDisposable
{
    private readonly string _server = Path.Combine(Path.GetTempPath(), "mineos-probe-" + Guid.NewGuid().ToString("N"));

    public MinecraftVersionProbeTests() => Directory.CreateDirectory(_server);

    public void Dispose()
    {
        try { Directory.Delete(_server, recursive: true); } catch { /* best effort */ }
    }

    private void JarWithVersion(string relative, string versionJson)
    {
        var path = Path.Combine(_server, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("version.json").Open());
        writer.Write(versionJson);
    }

    [Fact]
    public void Reads_Forge_And_NeoForge_Library_Layout()
    {
        Directory.CreateDirectory(Path.Combine(_server, "libraries", "net", "minecraft", "server", "1.20.1-20230612.114412"));

        Assert.Equal("1.20.1", MinecraftVersionProbe.FromDisk(_server, "@libraries/net/minecraftforge/forge/47.2.0/unix_args.txt"));
    }

    [Fact]
    public void Reads_The_Fabric_Remap_Cache()
    {
        Directory.CreateDirectory(Path.Combine(_server, ".fabric", "remappedJars", "minecraft-1.20.4-0.15.7"));

        Assert.Equal("1.20.4", MinecraftVersionProbe.FromDisk(_server, "fabric-server-launch.jar"));
    }

    [Fact]
    public void Reads_Version_Json_From_The_Vanilla_Jar()
    {
        JarWithVersion("server.jar", """{"id":"1.21.1","name":"1.21.1","world_version":3955}""");

        Assert.Equal("1.21.1", MinecraftVersionProbe.FromDisk(_server, "fabric-server-launch.jar"));
    }

    [Fact]
    public void Follows_The_Fabric_Launcher_Properties()
    {
        File.WriteAllText(Path.Combine(_server, "fabric-server-launcher.properties"), "#comment\nserverJar=vanilla/mc.jar\n");
        JarWithVersion("vanilla/mc.jar", """{"id":"1.19.2"}""");

        Assert.Equal("1.19.2", MinecraftVersionProbe.FromDisk(_server, "fabric-server-launch.jar"));
    }

    [Fact]
    public void Ignores_Jars_Outside_The_Server()
    {
        var outside = Path.Combine(Path.GetTempPath(), "mineos-probe-out-" + Guid.NewGuid().ToString("N") + ".jar");
        try
        {
            using (var zip = ZipFile.Open(outside, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("version.json").Open()))
            {
                writer.Write("""{"id":"9.9.9"}""");
            }

            Assert.Null(MinecraftVersionProbe.FromDisk(_server, outside));
            Assert.Null(MinecraftVersionProbe.FromDisk(_server, "../" + Path.GetFileName(outside)));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Nothing_On_Disk_Means_Unknown()
    {
        File.WriteAllText(Path.Combine(_server, "server.jar"), "not a zip");

        Assert.Null(MinecraftVersionProbe.FromDisk(_server, "server.jar"));
    }
}
