using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MineOS.Application.Dtos;
using MineOS.Application.Interfaces;
using MineOS.Application.Options;
using MineOS.Infrastructure.Services.Mods;

namespace MineOS.Tests.Unit;

public sealed class ModUpdateServiceTests : IDisposable
{
    private const string Server = "modded";

    private readonly string _base;
    private readonly string _mods;
    private readonly Mock<IServerService> _servers = new();
    private readonly Mock<IModrinthService> _modrinth = new();
    private readonly Mock<IModpackRepository> _repo = new();
    private readonly ModUpdateService _service;

    private static readonly byte[] OldJar = { 1, 2, 3 };
    private static readonly byte[] NewJar = { 4, 5, 6, 7 };

    public ModUpdateServiceTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "mineos-upd-" + Guid.NewGuid().ToString("N"));
        _mods = Path.Combine(_base, "servers", Server, "mods");
        Directory.CreateDirectory(_mods);
        File.WriteAllBytes(Path.Combine(_mods, "lithium-0.11.jar"), OldJar);

        _servers.Setup(s => s.DetectLoaderAsync(Server, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerLoaderDto("fabric", "0.15.7", "1.20.4"));

        var options = Options.Create(new HostOptions
        {
            BaseDirectory = _base,
            RunAsUid = Environment.IsPrivilegedProcess ? 0 : -1,
            RunAsGid = Environment.IsPrivilegedProcess ? 0 : -1
        });
        _service = new ModUpdateService(options, _servers.Object, _modrinth.Object, _repo.Object,
            Mock.Of<ILogger<ModUpdateService>>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    private static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    private static ModrinthVersionDto Version(string id, string number, DateTimeOffset published, string file, string? sha1 = null) =>
        new(id, "proj-lithium", number, number, published, 0, new[] { "1.20.4" }, new[] { "fabric" },
            new[] { new ModrinthVersionFileDto($"https://cdn.modrinth.com/data/x/{file}", file, 4, true, sha1) },
            Array.Empty<ModrinthDependencyDto>());

    private void GivenModrinth(ModrinthVersionDto installed, ModrinthVersionDto latest)
    {
        var hash = Sha1(OldJar);
        _modrinth.Setup(m => m.GetVersionsByFileHashesAsync(It.IsAny<IReadOnlyList<string>>(), "sha1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ModrinthVersionDto> { [hash] = installed });
        _modrinth.Setup(m => m.GetLatestVersionsByFileHashesAsync(
                It.IsAny<IReadOnlyList<string>>(), "sha1",
                It.Is<IReadOnlyList<string>>(l => l.Contains("fabric")),
                It.Is<IReadOnlyList<string>>(v => v.SequenceEqual(new[] { "1.20.4" })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ModrinthVersionDto> { [hash] = latest });
        _modrinth.Setup(m => m.OpenDownloadStreamAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(NewJar));
    }

    private static readonly DateTimeOffset Jan = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Mar = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Check_Lists_A_Newer_Compatible_Version()
    {
        GivenModrinth(Version("v1", "0.11", Jan, "lithium-0.11.jar"), Version("v2", "0.12", Mar, "lithium-0.12.jar"));

        var result = await _service.CheckAsync(Server, CancellationToken.None);

        var update = Assert.Single(result.Updates);
        Assert.Equal("lithium-0.11.jar", update.FileName);
        Assert.Equal("0.11", update.CurrentVersion);
        Assert.Equal("0.12", update.LatestVersion);
        Assert.Equal(1, result.ModsChecked);
        Assert.Equal(1, result.RecognizedOnModrinth);
    }

    [Fact]
    public async Task Check_Ignores_Same_Or_Older_Versions()
    {
        GivenModrinth(Version("v2", "0.12", Mar, "lithium-0.12.jar"), Version("v1", "0.11", Jan, "lithium-0.11.jar"));

        Assert.Empty((await _service.CheckAsync(Server, CancellationToken.None)).Updates);
    }

    [Fact]
    public async Task Check_Refuses_Without_A_Known_Minecraft_Version()
    {
        _servers.Setup(s => s.DetectLoaderAsync(Server, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerLoaderDto("fabric", null, null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CheckAsync(Server, CancellationToken.None));
    }

    [Fact]
    public async Task Update_Replaces_The_Jar_And_Keeps_The_Modpack_Record()
    {
        GivenModrinth(Version("v1", "0.11", Jan, "lithium-0.11.jar"),
            Version("v2", "0.12", Mar, "lithium-0.12.jar", Sha1(NewJar)));

        var result = await _service.UpdateAsync(Server, "lithium-0.11.jar", CancellationToken.None);

        Assert.Equal("lithium-0.12.jar", result.NewFileName);
        Assert.False(File.Exists(Path.Combine(_mods, "lithium-0.11.jar")));
        Assert.Equal(NewJar, File.ReadAllBytes(Path.Combine(_mods, "lithium-0.12.jar")));
        _repo.Verify(r => r.RenameModFileAsync(Server, "lithium-0.11.jar", "lithium-0.12.jar", It.IsAny<CancellationToken>()));
        _servers.Verify(s => s.MarkRestartRequiredAsync(Server, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Update_With_A_Bad_Checksum_Keeps_The_Old_Jar()
    {
        GivenModrinth(Version("v1", "0.11", Jan, "lithium-0.11.jar"),
            Version("v2", "0.12", Mar, "lithium-0.12.jar", sha1: "0000000000000000000000000000000000000000"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UpdateAsync(Server, "lithium-0.11.jar", CancellationToken.None));

        Assert.Equal(OldJar, File.ReadAllBytes(Path.Combine(_mods, "lithium-0.11.jar")));
        Assert.Equal(new[] { "lithium-0.11.jar" }, Directory.GetFiles(_mods).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("../escape.jar")]
    [InlineData("not-a-jar.txt")]
    public async Task Update_Rejects_Bad_Names(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateAsync(Server, name, CancellationToken.None));
    }

    [Fact]
    public async Task Update_Of_An_Up_To_Date_Mod_Is_A_Conflict()
    {
        GivenModrinth(Version("v1", "0.11", Jan, "lithium-0.11.jar"), Version("v1", "0.11", Jan, "lithium-0.11.jar"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UpdateAsync(Server, "lithium-0.11.jar", CancellationToken.None));
    }
}
