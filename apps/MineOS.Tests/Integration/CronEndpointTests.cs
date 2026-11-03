using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MineOS.Tests.Integration;

/// <summary>
/// Scheduled console commands type into the server console, so creating one must
/// need the Console permission — not just Control, which is all the cron routes
/// otherwise ask for.
/// </summary>
public class CronEndpointTests : IClassFixture<MineOsWebApplicationFactory>
{
    private const string ApiKey = "dev-static-api-key-change-me";
    private readonly HttpClient _client;

    public CronEndpointTests(MineOsWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Api-Key", ApiKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await _client.SendAsync(request);
    }

    private async Task<string> LoginAsync(string username, string password)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("accessToken").GetString()!;
    }

    /// <summary>Creates a non-admin user with the given rights on one server and logs them in.</summary>
    private async Task<string> UserWithAccessAsync(string server, bool canConsole)
    {
        var admin = await LoginAsync("admin", "admin123!");
        var username = $"cron-{Guid.NewGuid():N}"[..20];
        var created = await SendAsync(HttpMethod.Post, "/api/v1/auth/users", admin, new
        {
            username,
            password = "CronUser123!",
            role = "user",
            serverAccesses = new[]
            {
                new { serverName = server, canView = true, canControl = true, canConsole }
            }
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        return await LoginAsync(username, "CronUser123!");
    }

    private static string UniqueServer() => $"cron-srv-{Guid.NewGuid():N}"[..24];

    [Fact]
    public async Task Control_Without_Console_Cannot_Schedule_Commands()
    {
        var server = UniqueServer();
        var token = await UserWithAccessAsync(server, canConsole: false);

        var command = await SendAsync(HttpMethod.Post, $"/api/v1/servers/{server}/cron", token,
            new { source = "0 12 * * *", action = "command", command = "op someone" });
        Assert.Equal(HttpStatusCode.Forbidden, command.StatusCode);

        // The rest of the scheduler stays theirs.
        var restart = await SendAsync(HttpMethod.Post, $"/api/v1/servers/{server}/cron", token,
            new { source = "0 4 * * *", action = "restart", warningMinutes = 5 });
        Assert.Equal(HttpStatusCode.Created, restart.StatusCode);
    }

    [Fact]
    public async Task Console_Access_Can_Schedule_Commands()
    {
        var server = UniqueServer();
        var token = await UserWithAccessAsync(server, canConsole: true);

        var response = await SendAsync(HttpMethod.Post, $"/api/v1/servers/{server}/cron", token,
            new { source = "0 12 * * *", action = "command", command = "/say hello" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var job = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("command", job.GetProperty("action").GetString());
        Assert.Equal("say hello", job.GetProperty("command").GetString());
    }

    [Fact]
    public async Task Invalid_Command_Jobs_Are_Rejected()
    {
        var admin = await LoginAsync("admin", "admin123!");
        var server = UniqueServer();

        var missing = await SendAsync(HttpMethod.Post, $"/api/v1/servers/{server}/cron", admin,
            new { source = "0 12 * * *", action = "command" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var twoLines = await SendAsync(HttpMethod.Post, $"/api/v1/servers/{server}/cron", admin,
            new { source = "0 12 * * *", action = "command", command = "say hi\nstop" });
        Assert.Equal(HttpStatusCode.BadRequest, twoLines.StatusCode);

        var warnedBackup = await SendAsync(HttpMethod.Post, $"/api/v1/servers/{server}/cron", admin,
            new { source = "0 12 * * *", action = "backup", warningMinutes = 5 });
        Assert.Equal(HttpStatusCode.BadRequest, warnedBackup.StatusCode);
    }

    [Fact]
    public async Task Restart_Warning_Is_Returned_With_The_Job()
    {
        var admin = await LoginAsync("admin", "admin123!");
        var server = UniqueServer();

        var created = await SendAsync(HttpMethod.Post, $"/api/v1/servers/{server}/cron", admin,
            new { source = "0 4 * * *", action = "restart", warningMinutes = 15 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var list = await SendAsync(HttpMethod.Get, $"/api/v1/servers/{server}/cron", admin);
        var jobs = await list.Content.ReadFromJsonAsync<JsonElement>();
        var job = Assert.Single(jobs.EnumerateArray());
        Assert.Equal(15, job.GetProperty("warningMinutes").GetInt32());
    }
}
