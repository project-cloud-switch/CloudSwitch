using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CloudSwitch.Api.Authentication;

public static class AuthenticationExtensions
{
    public static IServiceCollection AddApiAuthentication(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<AuthenticationOptions>()
            .Bind(configuration.GetSection(AuthenticationOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => Uri.TryCreate(options.Authority, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
                && string.IsNullOrEmpty(uri.UserInfo), "Authentication:Authority must be an absolute HTTP(S) issuer URL.")
            .Validate(options => environment.IsDevelopment()
                || (options.RequireHttpsMetadata && options.Authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase)),
                "Authentication requires HTTPS outside Development.")
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthenticationOptions>>((jwt, settings) =>
            {
                jwt.Authority = settings.Value.Authority;
                jwt.Audience = settings.Value.Audience;
                jwt.RequireHttpsMetadata = settings.Value.RequireHttpsMetadata;
                jwt.MapInboundClaims = false;
                jwt.IncludeErrorDetails = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    NameClaimType = "preferred_username",
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });

        var policy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
                context.User.FindFirst("iss")?.Value is { Length: > 0 and <= 512 } issuer
                && !string.IsNullOrWhiteSpace(issuer)
                && context.User.FindFirst("sub")?.Value is { Length: > 0 and <= 512 } subject
                && !string.IsNullOrWhiteSpace(subject))
            .Build();
        services.AddAuthorizationBuilder().SetDefaultPolicy(policy).SetFallbackPolicy(policy);
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddScoped<ICurrentAccount, CurrentAccount>();
        return services;
    }
}
