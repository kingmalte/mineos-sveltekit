using System.Formats.Tar;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MineOS.Tests.Unit;

namespace MineOS.Tests.Integration;

public class ModToolsEndpointTests : IClassFixture<MineOsWebApplicationFactory>
{
    private const string ApiKey = "dev-static-api-key-change-me";
    private readonly MineOsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ModToolsEndpointTests(MineOsWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Add("X-Api-Key", ApiKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private async Task<string> LoginAsync(string username, string password)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private async Task<(string Admin, string Server)> ServerAsync()
    {
        var admin = await LoginAsync("admin", "admin123!");
        var created = await SendAsync(HttpMethod.Post, "/api/v1/servers", admin,
            JsonContent.Create(new { name = $"mods-{Guid.NewGuid():N}"[..20], serverType = "bedrock" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var server = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString()!;
        // A fabric server as far as mod checks are concerned.
        var loader = await SendAsync(HttpMethod.Put, $"/api/v1/servers/{server}/loader", admin,
            JsonContent.Create(new { loader = "fabric" }));
        Assert.Equal(HttpStatusCode.OK, loader.StatusCode);
        return (admin, server);
    }

    private string ModsDir(string server) => Path.Combine(_factory.HostRoot, "servers", server, "mods");

    private static MultipartFormDataContent Upload(string fileName, byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", fileName);
        return form;
    }

    [Fact]
    public async Task Upload_Flags_Client_Only_Mods_And_They_Can_Be_Moved()
    {
        var (admin, server) = await ServerAsync();
        var sodium = TestJars.Build(("fabric.mod.json", TestJars.Fabric("sodium", "client")));

        var upload = await SendAsync(HttpMethod.Post, $"/api/v1/servers/{server}/mods/upload", admin, Upload("sodium.jar", sodium));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var body = await upload.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sodium.jar", Assert.Single(body.GetProperty("clientOnlyFiles").EnumerateArray()).GetString());

        var move = await SendAsync(HttpMethod.Post, $"/api/v1/servers/{server}/mods/sodium.jar/move-to-client", admin);
        Assert.Equal(HttpStatusCode.OK, move.StatusCode);
        Assert.False(File.Exists(Path.Combine(ModsDir(server), "sodium.jar")));
        Assert.True(File.Exists(Path.Combine(_factory.HostRoot, "servers", server, "client-mods", "mods", "sodium.jar")));
    }

    [Fact]
    public async Task Check_Reports_Missing_Dependencies_Over_Http()
    {
        var (admin, server) = await ServerAsync();
        var needy = TestJars.Build(("fabric.mod.json", TestJars.Fabric("needy", depends: """{ "fabric-api": "*" }""")));
        await SendAsync(HttpMethod.Post, $"/api/v1/servers/{server}/mods/upload", admin, Upload("needy.jar", needy));

        var check = await SendAsync(HttpMethod.Get, $"/api/v1/servers/{server}/mods/check", admin);
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        var result = await check.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("fabric", result.GetProperty("serverLoader").GetString());
        var issue = Assert.Single(result.GetProperty("issues").EnumerateArray());
        Assert.Equal("missing-dependency", issue.GetProperty("kind").GetString());
        Assert.Contains("fabric-api", issue.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Batch_Delete_Validates_First_And_Reports_Per_File()
    {
        var (admin, server) = await ServerAsync();
        Directory.CreateDirectory(ModsDir(server));
        File.WriteAllText(Path.Combine(ModsDir(server), "a.jar"), "a");
        File.WriteAllText(Path.Combine(ModsDir(server), "b.jar"), "b");

        var bad = await SendAsync(HttpMethod.Post, $"/api/v1/servers/{server}/mods/delete-batch", admin,
            JsonContent.Create(new { fileNames = new[] { "a.jar", "../../server.properties" } }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.True(File.Exists(Path.Combine(ModsDir(server), "a.jar")), "nothing is deleted when one name is invalid");

        var ok = await SendAsync(HttpMethod.Post, $"/api/v1/servers/{server}/mods/delete-batch", admin,
            JsonContent.Create(new { fileNames = new[] { "a.jar", "b.jar", "gone.jar" } }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var result = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, result.GetProperty("deleted").GetArrayLength());
        Assert.Equal("gone.jar", Assert.Single(result.GetProperty("failed").EnumerateArray()).GetProperty("fileName").GetString());
        Assert.Empty(Directory.GetFiles(ModsDir(server)));
    }

    [Fact]
    public async Task Toggle_Conflict_Is_A_409_With_A_Message()
    {
        var (admin, server) = await ServerAsync();
        Directory.CreateDirectory(ModsDir(server));
        File.WriteAllText(Path.Combine(ModsDir(server), "x.jar"), "1");
        File.WriteAllText(Path.Combine(ModsDir(server), "x.jar.disabled"), "2");

        var toggle = await SendAsync(HttpMethod.Post, $"/api/v1/servers/{server}/mods/x.jar/disable", admin);

        Assert.Equal(HttpStatusCode.Conflict, toggle.StatusCode);
        Assert.Contains("already exists", (await toggle.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Tar_Upload_With_A_Symlink_Does_Not_Leak_Host_Files()
    {
        var (admin, server) = await ServerAsync();
        var secret = Path.Combine(_factory.HostRoot, $"secret-{Guid.NewGuid():N}.txt");
        File.WriteAllText(secret, "host secret");

        var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "pack/innocent-mod.jar") { LinkName = secret });
        }

        var upload = await SendAsync(HttpMethod.Post, $"/api/v1/servers/{server}/mods/upload", admin,
            Upload("mods.tar", tar.ToArray()));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var download = await SendAsync(HttpMethod.Get, $"/api/v1/servers/{server}/mods/innocent-mod.jar/download", admin);
        Assert.NotEqual(HttpStatusCode.OK, download.StatusCode);
        Assert.False(File.Exists(Path.Combine(ModsDir(server), "innocent-mod.jar")));
    }

    [Fact]
    public async Task View_Only_Users_Cannot_Change_Mods()
    {
        var (admin, server) = await ServerAsync();
        var username = $"mv-{Guid.NewGuid():N}"[..20];
        await SendAsync(HttpMethod.Post, "/api/v1/auth/users", admin, JsonContent.Create(new
        {
            username,
            password = "ModView123!",
            role = "user",
            serverAccesses = new[] { new { serverName = server, canView = true, canControl = false, canConsole = false } }
        }));
        var viewer = await LoginAsync(username, "ModView123!");

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/api/v1/servers/{server}/mods/check", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, $"/api/v1/servers/{server}/mods/delete-batch", viewer,
            JsonContent.Create(new { fileNames = new[] { "a.jar" } }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, $"/api/v1/servers/{server}/loader", viewer,
            JsonContent.Create(new { loader = "forge" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, $"/api/v1/servers/{server}/mods/a.jar/update", viewer)).StatusCode);
    }
}
