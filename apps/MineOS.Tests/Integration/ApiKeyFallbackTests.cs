using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace MineOS.Tests.Integration;

/// <summary>
/// The web app sends the service API key on every request it proxies, next to
/// the user's own token. The key is an admin identity, so it must only count
/// when no user token was presented at all — never as a fallback for a user
/// token that failed validation, or any user could turn into an admin by
/// sending a broken token.
/// </summary>
public class ApiKeyFallbackTests : IClassFixture<MineOsWebApplicationFactory>
{
    private const string ApiKey = "dev-static-api-key-change-me";
    private readonly HttpClient _client;

    public ApiKeyFallbackTests(MineOsWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? bearer, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Api-Key", ApiKey);
        if (bearer != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        if (body != null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await _client.SendAsync(request);
    }

    private static string SignedToken(DateTime expires, string signingKey = MineOsWebApplicationFactory.TestSigningKey)
    {
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "MineOS.Test",
            audience: "MineOS.Test",
            claims: new[]
            {
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, "admin"),
                new Claim(ClaimTypes.NameIdentifier, "1"),
            },
            notBefore: expires.AddHours(-2),
            expires: expires,
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<string> LoginAsync(string username, string password)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("accessToken").GetString()!;
    }

    [Fact]
    public async Task Api_Key_Without_Token_Is_Still_Admin()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/v1/auth/users", bearer: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("a.b.c")]
    public async Task Malformed_Token_Is_Not_Upgraded_By_Api_Key(string bearer)
    {
        var response = await SendAsync(HttpMethod.Get, "/api/v1/auth/users", bearer);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Expired_Token_Is_Not_Upgraded_By_Api_Key()
    {
        var expired = SignedToken(DateTime.UtcNow.AddHours(-1));
        var response = await SendAsync(HttpMethod.Get, "/api/v1/auth/users", expired);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_With_Wrong_Signature_Is_Not_Upgraded_By_Api_Key()
    {
        var forged = SignedToken(DateTime.UtcNow.AddHours(1), "some-other-signing-key-at-least-32-characters!!");
        var response = await SendAsync(HttpMethod.Get, "/api/v1/auth/users", forged);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Non_Admin_Stays_Non_Admin_With_Api_Key_Alongside()
    {
        var adminToken = await LoginAsync("admin", "admin123!");
        var username = $"plain-{Guid.NewGuid():N}"[..20];
        var created = await SendAsync(HttpMethod.Post, "/api/v1/auth/users", adminToken,
            new { username, password = "PlainUser123!", role = "user" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var userToken = await LoginAsync(username, "PlainUser123!");

        var users = await SendAsync(HttpMethod.Get, "/api/v1/auth/users", userToken);
        Assert.Equal(HttpStatusCode.Forbidden, users.StatusCode);

        var promote = await SendAsync(HttpMethod.Post, "/api/v1/auth/users", userToken,
            new { username = username + "x", password = "PlainUser123!", role = "admin" });
        Assert.Equal(HttpStatusCode.Forbidden, promote.StatusCode);
    }
}
