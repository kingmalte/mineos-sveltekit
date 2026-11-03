using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MineOS.Tests.Integration;

/// <summary>
/// The file manager over HTTP, with the two attacks that worked before: reaching
/// the host through a symlink, and deleting the whole server directory with a
/// path that trimmed down to "/".
/// </summary>
public class FileEndpointTests : IClassFixture<MineOsWebApplicationFactory>
{
    private const string ApiKey = "dev-static-api-key-change-me";
    private readonly MineOsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public FileEndpointTests(MineOsWebApplicationFactory factory)
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
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("accessToken").GetString()!;
    }

    private Task<string> AdminAsync() => LoginAsync("admin", "admin123!");

    private async Task<string> CreateServerAsync(string token)
    {
        var label = $"files-{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/v1/servers", token,
            JsonContent.Create(new { name = label, serverType = "bedrock" }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("name").GetString()!;
    }

    private string ServerDir(string server) => Path.Combine(_factory.HostRoot, "servers", server);

    private async Task<string> UserAsync(string server, bool canControl)
    {
        var admin = await AdminAsync();
        var username = $"fm-{Guid.NewGuid():N}"[..20];
        var created = await SendAsync(HttpMethod.Post, "/api/v1/auth/users", admin, JsonContent.Create(new
        {
            username,
            password = "FileUser123!",
            role = "user",
            serverAccesses = new[] { new { serverName = server, canView = true, canControl, canConsole = false } }
        }));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        return await LoginAsync(username, "FileUser123!");
    }

    [Theory]
    [InlineData("%20")]
    [InlineData("%2e")]
    [InlineData("logs%2f..")]
    public async Task Server_Directory_Survives_Delete_Of_Root_Like_Paths(string path)
    {
        var admin = await AdminAsync();
        var server = await CreateServerAsync(admin);
        var controlUser = await UserAsync(server, canControl: true);

        await SendAsync(HttpMethod.Delete, $"/api/v1/servers/{server}/files/{path}", controlUser);

        Assert.True(Directory.Exists(ServerDir(server)), "the server directory was deleted");
    }

    [Fact]
    public async Task Symlink_To_The_Host_Is_Refused()
    {
        var admin = await AdminAsync();
        var server = await CreateServerAsync(admin);
        var outside = Path.Combine(_factory.HostRoot, $"host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "host secret");
        Directory.CreateSymbolicLink(Path.Combine(ServerDir(server), "plugins-data"), outside);
        var viewer = await UserAsync(server, canControl: false);
        var controlUser = await UserAsync(server, canControl: true);

        var read = await SendAsync(HttpMethod.Get, $"/api/v1/servers/{server}/files/plugins-data/secret.txt", viewer);
        Assert.Equal(HttpStatusCode.BadRequest, read.StatusCode);
        Assert.DoesNotContain("host secret", await read.Content.ReadAsStringAsync());

        var download = await SendAsync(HttpMethod.Get,
            $"/api/v1/servers/{server}/files/plugins-data/secret.txt?download=true", viewer);
        Assert.Equal(HttpStatusCode.BadRequest, download.StatusCode);

        var write = await SendAsync(HttpMethod.Put, $"/api/v1/servers/{server}/files/plugins-data/planted.txt",
            controlUser, JsonContent.Create(new { content = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, write.StatusCode);
        Assert.False(File.Exists(Path.Combine(outside, "planted.txt")));
    }

    [Fact]
    public async Task Downloads_A_File_And_A_Folder_As_Zip()
    {
        var admin = await AdminAsync();
        var server = await CreateServerAsync(admin);
        Directory.CreateDirectory(Path.Combine(ServerDir(server), "world", "region"));
        File.WriteAllText(Path.Combine(ServerDir(server), "world", "region", "r.0.0.mca"), "chunks");

        var file = await SendAsync(HttpMethod.Get,
            $"/api/v1/servers/{server}/files/world/region/r.0.0.mca?download=true", admin);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("r.0.0.mca", file.Content.Headers.ContentDisposition?.FileNameStar
            ?? file.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("chunks", await file.Content.ReadAsStringAsync());

        var folder = await SendAsync(HttpMethod.Get, $"/api/v1/servers/{server}/files/world?download=true", admin);
        Assert.Equal(HttpStatusCode.OK, folder.StatusCode);
        Assert.Equal("application/zip", folder.Content.Headers.ContentType?.MediaType);
        using var zip = new ZipArchive(new MemoryStream(await folder.Content.ReadAsByteArrayAsync()));
        Assert.Contains(zip.Entries, e => e.FullName == "region/r.0.0.mca");
    }

    [Fact]
    public async Task Create_Move_And_Copy_Through_The_Api()
    {
        var admin = await AdminAsync();
        var server = await CreateServerAsync(admin);
        var controlUser = await UserAsync(server, canControl: true);
        var baseUrl = $"/api/v1/servers/{server}/file-actions";

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"{baseUrl}/create-folder", controlUser,
            JsonContent.Create(new { path = "/plugins/Demo" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"{baseUrl}/create-file", controlUser,
            JsonContent.Create(new { path = "/plugins/Demo/config.yml" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{baseUrl}/create-file", controlUser,
            JsonContent.Create(new { path = "/plugins/Demo/config.yml" }))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"{baseUrl}/copy", controlUser,
            JsonContent.Create(new { from = "/plugins/Demo", to = "/plugins/Demo-copy" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"{baseUrl}/move", controlUser,
            JsonContent.Create(new { from = "/plugins/Demo-copy/config.yml", to = "/plugins/Demo-copy/renamed.yml" }))).StatusCode);

        Assert.True(File.Exists(Path.Combine(ServerDir(server), "plugins", "Demo", "config.yml")));
        Assert.True(File.Exists(Path.Combine(ServerDir(server), "plugins", "Demo-copy", "renamed.yml")));

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, $"{baseUrl}/move", controlUser,
            JsonContent.Create(new { from = "/", to = "/gone" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"{baseUrl}/move", controlUser,
            JsonContent.Create(new { from = "/missing.txt", to = "/x.txt" }))).StatusCode);
    }

    [Fact]
    public async Task View_Only_Users_Cannot_Change_Files()
    {
        var admin = await AdminAsync();
        var server = await CreateServerAsync(admin);
        var viewer = await UserAsync(server, canControl: false);

        var create = await SendAsync(HttpMethod.Post, $"/api/v1/servers/{server}/file-actions/create-file", viewer,
            JsonContent.Create(new { path = "/x.txt" }));
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        var move = await SendAsync(HttpMethod.Post, $"/api/v1/servers/{server}/file-actions/move", viewer,
            JsonContent.Create(new { from = "/server.properties", to = "/y" }));
        Assert.Equal(HttpStatusCode.Forbidden, move.StatusCode);
    }

    [Fact]
    public async Task Upload_Streams_Large_Bodies_To_Disk()
    {
        var admin = await AdminAsync();
        var server = await CreateServerAsync(admin);
        var data = new byte[8 * 1024 * 1024];
        Random.Shared.NextBytes(data);

        var upload = await SendAsync(HttpMethod.Post, $"/api/v1/servers/{server}/files/plugins/big.jar", admin,
            new ByteArrayContent(data) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } });

        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(ServerDir(server), "plugins", "big.jar")));
    }
}
