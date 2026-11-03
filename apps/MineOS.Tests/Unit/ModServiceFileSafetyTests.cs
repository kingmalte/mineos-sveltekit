using System.Formats.Tar;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MineOS.Application.Interfaces;
using MineOS.Application.Options;
using MineOS.Infrastructure.Services;

namespace MineOS.Tests.Unit;

/// <summary>
/// Mod uploads and downloads run as root on the host. These pin down that an
/// uploaded archive or a planted symlink can never make them read or write a file
/// outside the server directory.
/// </summary>
public sealed class ModServiceFileSafetyTests : IDisposable
{
    private const string Server = "modded";

    private readonly string _base;
    private readonly string _serverDir;
    private readonly string _modsDir;
    private readonly string _outside;
    private readonly string _secret;
    private readonly ModService _mods;

    public ModServiceFileSafetyTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "mineos-mods-" + Guid.NewGuid().ToString("N"));
        _serverDir = Path.Combine(_base, "servers", Server);
        _modsDir = Path.Combine(_serverDir, "mods");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(_serverDir);
        Directory.CreateDirectory(_outside);
        _secret = Path.Combine(_outside, "secret.txt");
        System.IO.File.WriteAllText(_secret, "host secret");

        var options = Options.Create(new HostOptions
        {
            BaseDirectory = _base,
            RunAsUid = Environment.IsPrivilegedProcess ? 0 : -1,
            RunAsGid = Environment.IsPrivilegedProcess ? 0 : -1
        });
        _mods = new ModService(
            Mock.Of<ILogger<ModService>>(),
            options,
            Mock.Of<ICurseForgeService>(),
            Mock.Of<IModrinthService>(),
            Mock.Of<ISettingsService>(),
            Mock.Of<IServerService>(),
            Mock.Of<IProfileService>(),
            new HttpClient(),
            Mock.Of<IModpackRepository>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    private static MemoryStream Tar(bool gzip, params TarEntry[] entries)
    {
        var raw = new MemoryStream();
        using (var writer = new TarWriter(raw, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                writer.WriteEntry(entry);
            }
        }
        raw.Position = 0;
        if (!gzip)
        {
            return raw;
        }

        var compressed = new MemoryStream();
        using (var gz = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            raw.CopyTo(gz);
        }
        compressed.Position = 0;
        return compressed;
    }

    private static PaxTarEntry Regular(string name, string content) =>
        new(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)) };

    private static PaxTarEntry SymLink(string name, string target) =>
        new(TarEntryType.SymbolicLink, name) { LinkName = target };

    private static PaxTarEntry HardLink(string name, string target) =>
        new(TarEntryType.HardLink, name) { LinkName = target };

    [Theory]
    [InlineData("mods.tar", false)]
    [InlineData("mods.tar.gz", true)]
    [InlineData("mods.tgz", true)]
    public async Task Tar_Upload_Copies_Real_Jars_Only(string archiveName, bool gzip)
    {
        using var archive = Tar(gzip,
            Regular("pack/real-mod.jar", "jar bytes"),
            Regular("pack/readme.txt", "not a mod"),
            SymLink("pack/innocent-mod.jar", _secret),
            HardLink("pack/hard-mod.jar", _secret));

        await _mods.SaveModAsync(Server, archiveName, archive, CancellationToken.None);

        Assert.Equal(new[] { "real-mod.jar" }, Directory.GetFiles(_modsDir).Select(Path.GetFileName));
        Assert.Equal("jar bytes", System.IO.File.ReadAllText(Path.Combine(_modsDir, "real-mod.jar")));
    }

    [Fact]
    public async Task Tar_Upload_Cannot_Escape_With_Dot_Dot_Names()
    {
        using var archive = Tar(false, Regular("../../../../outside/evil.jar", "x"));

        await _mods.SaveModAsync(Server, "mods.tar", archive, CancellationToken.None);

        Assert.False(System.IO.File.Exists(Path.Combine(_outside, "evil.jar")));
        Assert.True(System.IO.File.Exists(Path.Combine(_modsDir, "evil.jar")));
    }

    [Fact]
    public async Task Broken_Tar_Is_A_Bad_Request_Not_A_Crash()
    {
        using var garbage = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        await Assert.ThrowsAnyAsync<Exception>(() =>
            _mods.SaveModAsync(Server, "mods.tar.gz", garbage, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_modsDir));
    }

    [Fact]
    public async Task Zip_Upload_Flattens_To_Jar_Names()
    {
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(archive.CreateEntry("deep/dir/a.jar").Open())) w.Write("a");
            using (var w = new StreamWriter(archive.CreateEntry("../../escape.jar").Open())) w.Write("b");
            using (var w = new StreamWriter(archive.CreateEntry("notes.md").Open())) w.Write("c");
        }
        zip.Position = 0;

        await _mods.SaveModAsync(Server, "pack.zip", zip, CancellationToken.None);

        Assert.Equal(new[] { "a.jar", "escape.jar" },
            Directory.GetFiles(_modsDir).Select(Path.GetFileName).OrderBy(n => n));
    }

    [Fact]
    public async Task Planted_Link_In_Mods_Is_Not_Downloadable()
    {
        Directory.CreateDirectory(_modsDir);
        System.IO.File.CreateSymbolicLink(Path.Combine(_modsDir, "x.jar"), _secret);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _mods.GetModPathAsync(Server, "x.jar", CancellationToken.None));
    }

    [Fact]
    public async Task Upload_Replaces_A_Planted_Link_Instead_Of_Writing_Through_It()
    {
        Directory.CreateDirectory(_modsDir);
        System.IO.File.CreateSymbolicLink(Path.Combine(_modsDir, "x.jar"), _secret);

        await _mods.SaveModAsync(Server, "x.jar", new MemoryStream(new byte[] { 1, 2, 3 }), CancellationToken.None);

        Assert.Equal("host secret", System.IO.File.ReadAllText(_secret));
        Assert.Null(new FileInfo(Path.Combine(_modsDir, "x.jar")).LinkTarget);
        Assert.Equal(new byte[] { 1, 2, 3 }, System.IO.File.ReadAllBytes(Path.Combine(_modsDir, "x.jar")));
    }

    [Fact]
    public async Task A_Mods_Folder_Linked_Out_Of_The_Server_Is_Refused()
    {
        Directory.CreateSymbolicLink(_modsDir, _outside);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _mods.SaveModAsync(Server, "x.jar", new MemoryStream(new byte[] { 1 }), CancellationToken.None));
        Assert.False(System.IO.File.Exists(Path.Combine(_outside, "x.jar")));
    }

    [Fact]
    public async Task Toggling_Onto_An_Existing_Name_Is_A_Conflict()
    {
        Directory.CreateDirectory(_modsDir);
        System.IO.File.WriteAllText(Path.Combine(_modsDir, "x.jar"), "enabled copy");
        System.IO.File.WriteAllText(Path.Combine(_modsDir, "x.jar.disabled"), "disabled copy");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _mods.SetModEnabledAsync(Server, "x.jar", enabled: false, CancellationToken.None));
        Assert.Equal("enabled copy", System.IO.File.ReadAllText(Path.Combine(_modsDir, "x.jar")));
    }
}
