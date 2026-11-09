using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using MassTransit;
using CloudSwitch.Api.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CloudSwitch.Api.Tests;

public class ApiFactory : WebApplicationFactory<Program>
{
    protected virtual bool UseRealPersistence => false;
    public const string Issuer = "https://identity.example.test/realms/cloudswitch";
    public const string Audience = "cloudswitch-api";
    private readonly RSA signingKey = RSA.Create(2048);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Authority"] = Issuer,
                ["Authentication:RequireHttpsMetadata"] = "true",
                ["Logging:LogLevel:Default"] = "Critical",
                // Rate limiter tests supply small budgets on their own host instances.
                ["RateLimiting:AuthenticatedRequestsPerMinute"] = "10000",
                ["RateLimiting:AnonymousRequestsPerMinute"] = "10000",
                ["RateLimiting:JobSubmissionsPerMinute"] = "10000"
            });
            if (!UseRealPersistence)
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Never let host/authentication tests touch the developer's live database.
                    ["ConnectionStrings:Postgres"] = "Host=localhost;Port=65530;Database=unused;Username=unused;Password=unused;Timeout=1;Command Timeout=1"
                });
        });
        builder.ConfigureTestServices(services =>
        {
            if (!UseRealPersistence)
            {
                services.AddMassTransitTestHarness();
                services.AddScoped<ICurrentAccount, TestCurrentAccount>();
            }
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                configuration.SigningKeys.Add(new RsaSecurityKey(signingKey) { KeyId = "test-key" });
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            });
        });
    }

    public string CreateToken(string? issuer = null, string? audience = null,
        string? subject = "opaque-external-subject", DateTime? expires = null,
        bool includeExpiry = true, bool signed = true, bool wrongKey = false)
    {
        var claims = new List<Claim> { new("preferred_username", "test-user") };
        if (subject is not null)
            claims.Add(new Claim("sub", subject));
        using var otherKey = wrongKey ? RSA.Create(2048) : null;
        var token = new JwtSecurityToken(
            issuer: issuer ?? Issuer,
            audience: audience ?? Audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddHours(-1),
            expires: includeExpiry ? expires ?? DateTime.UtcNow.AddMinutes(5) : null,
            signingCredentials: signed
                ? new SigningCredentials(new RsaSecurityKey(otherKey ?? signingKey) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256)
                : null);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            signingKey.Dispose();
    }

    private sealed class TestCurrentAccount(ICurrentUser currentUser) : ICurrentAccount
    {
        public Task<Account> GetAsync(CancellationToken ct)
        {
            var identity = currentUser.Identity!;
            return Task.FromResult(new Account(Guid.Parse("11111111-1111-1111-1111-111111111111"),
                identity.Issuer, identity.Subject, identity.Username));
        }
    }
}
