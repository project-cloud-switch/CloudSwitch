using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CloudSwitch.Api.Authentication;
using Xunit;

namespace CloudSwitch.Api.Tests;

public sealed class AuthenticationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("application/json")]
    [InlineData("text/html")]
    public async Task Anonymous_requests_are_challenged_with_problem_details(string accept)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        using var response = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Signed_access_token_preserves_opaque_subject_and_issuer()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken());
        var identity = await client.GetFromJsonAsync<ExternalIdentity>("/api/v1/me");
        Assert.NotNull(identity);
        Assert.Equal(ApiFactory.Issuer, identity.Issuer);
        Assert.Equal("opaque-external-subject", identity.Subject);
        Assert.Equal("test-user", identity.Username);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("no-expiry")]
    [InlineData("unsigned")]
    [InlineData("signature")]
    [InlineData("malformed")]
    public async Task Invalid_tokens_are_rejected(string failure)
    {
        var token = failure switch
        {
            "issuer" => factory.CreateToken(issuer: "https://untrusted.example.test"),
            "audience" => factory.CreateToken(audience: "another-api"),
            "expired" => factory.CreateToken(expires: DateTime.UtcNow.AddMinutes(-5)),
            "no-expiry" => factory.CreateToken(includeExpiry: false),
            "unsigned" => factory.CreateToken(signed: false),
            "signature" => factory.CreateToken(wrongKey: true),
            _ => "not-a-jwt"
        };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Tokens_without_a_usable_subject_cannot_access_owned_resources(string? subject)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken(subject: subject));
        using var response = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
