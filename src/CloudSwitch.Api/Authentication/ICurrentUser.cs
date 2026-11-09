namespace CloudSwitch.Api.Authentication;

// CurrentAccount maps this identity to an application-owned UserId. OIDC subjects remain opaque strings.
public sealed record ExternalIdentity(string Issuer, string Subject, string? Username);

public interface ICurrentUser
{
    ExternalIdentity? Identity { get; }
}

internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public ExternalIdentity? Identity
    {
        get
        {
            var principal = accessor.HttpContext?.User;
            var issuer = principal?.FindFirst("iss")?.Value;
            var subject = principal?.FindFirst("sub")?.Value;

            return principal?.Identity?.IsAuthenticated == true
                && !string.IsNullOrWhiteSpace(issuer) && !string.IsNullOrWhiteSpace(subject)
                ? new ExternalIdentity(issuer, subject, principal.FindFirst("preferred_username")?.Value)
                : null;
        }
    }
}
