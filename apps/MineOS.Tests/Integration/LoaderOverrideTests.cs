using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MineOS.Tests.Integration;

/// <summary>A manually chosen mod loader is stored on the server, not in one browser.</summary>
public class LoaderOverrideTests : IClassFixture<MineOsWebApplicationFactory>
{
    private const string ApiKey = "dev-static-api-key-change-me";
    private readonly HttpClient _client;

    public LoaderOverrideTests(MineOsWebApplicationFactory factory) => _client = factory.CreateClient();

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Api-Key", ApiKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private async Task<string> LoginAsync(string username, string password)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    [Fact]
    public async Task Chosen_Loader_Is_Reported_As_Manual_And_Can_Be_Cleared()
    {
        var admin = await LoginAsync("admin", "admin123!");
        var created = await SendAsync(HttpMethod.Post, "/api/v1/servers", admin,
            new { name = $"loader-{Guid.NewGuid():N}"[..20], serverType = "bedrock" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var server = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString()!;

        var before = await (await SendAsync(HttpMethod.Get, $"/api/v1/servers/{server}/loader", admin))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, before.GetProperty("loader").ValueKind);

        var set = await SendAsync(HttpMethod.Put, $"/api/v1/servers/{server}/loader", admin, new { loader = "fabric" });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        var after = await (await SendAsync(HttpMethod.Get, $"/api/v1/servers/{server}/loader", admin))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("fabric", after.GetProperty("loader").GetString());
        Assert.True(after.GetProperty("manual").GetBoolean());

        var bad = await SendAsync(HttpMethod.Put, $"/api/v1/servers/{server}/loader", admin, new { loader = "rift" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        await SendAsync(HttpMethod.Put, $"/api/v1/servers/{server}/loader", admin, new { loader = (string?)null });
        var cleared = await (await SendAsync(HttpMethod.Get, $"/api/v1/servers/{server}/loader", admin))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("loader").ValueKind);
    }
}
