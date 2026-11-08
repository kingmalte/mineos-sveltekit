using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MineOS.Infrastructure.Persistence;

namespace MineOS.Tests.Integration;

public class AuthEndpointTests : IClassFixture<MineOsWebApplicationFactory>
{
    private readonly MineOsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AuthEndpointTests(MineOsWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Api-Key", "dev-static-api-key-change-me");
    }

    [Fact]
    public async Task Login_With_Valid_Credentials_Returns_Token()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "admin", password = "admin123!" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("accessToken", out var token));
        Assert.False(string.IsNullOrEmpty(token.GetString()));
    }

    [Fact]
    public async Task Login_With_Invalid_Credentials_Returns_401()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "admin", password = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Protected_Endpoint_With_Api_Key_Returns_200()
    {
        // The client carries the static X-Api-Key (see ctor). A valid service key
        // is a full-access identity, so it authenticates protected endpoints even
        // without a JWT. (Previously this was rejected — see the middleware ordering
        // fix that establishes an admin principal for a valid key.)
        var response = await _client.GetAsync("/api/v1/account");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Protected_Endpoint_With_Auth_Returns_200()
    {
        var loginResponse = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "admin", password = "admin123!" });
        var loginJson = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        var token = loginJson.GetProperty("accessToken").GetString();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/account");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Api-Key", "dev-static-api-key-change-me");
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Protected_Endpoint_Without_Api_Key_Returns_401()
    {
        using var noKeyClient = _factory.CreateClient();
        // No X-Api-Key header — protected endpoints should reject
        var response = await noKeyClient.GetAsync("/api/v1/account");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<string> LoginAsync()
    {
        var loginResponse = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "admin", password = "admin123!" });
        var loginJson = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        return loginJson.GetProperty("accessToken").GetString()!;
    }

    [Fact]
    public async Task Refresh_With_Valid_Token_Returns_New_Token_With_Issued_At()
    {
        var token = await LoginAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var refreshed = json.GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrEmpty(refreshed));
        Assert.True(json.GetProperty("expiresInSeconds").GetInt32() > 0);

        // The web app reads iat to decide when a session is due for renewal.
        var parsed = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(refreshed);
        Assert.Contains(parsed.Claims, c => c.Type == "iat");
        Assert.Equal("admin", parsed.Claims.First(c => c.Type.EndsWith("/name") || c.Type == "unique_name").Value);
    }

    [Fact]
    public async Task Refresh_With_Only_Api_Key_Returns_401()
    {
        // The service key has no user behind it, so it must not mint user tokens.
        var response = await _client.PostAsync("/api/v1/auth/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_With_Invalid_Token_Returns_401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-jwt");
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_For_Deactivated_User_Returns_401()
    {
        var token = await LoginAsync();

        await SetAdminActiveAsync(false);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var response = await _client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await SetAdminActiveAsync(true);
        }
    }

    private async Task SetAdminActiveAsync(bool active)
    {
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Users.First(u => u.Username == "admin").IsActive = active;
        await db.SaveChangesAsync();
    }
}
