namespace CloudSwitch.Persistence.Domain;

public sealed class User
{
    private const int MaxUsernameLength = 256;

    public Guid Id { get; private set; }
    public string Issuer { get; private set; }
    public string Subject { get; private set; }
    public string? Username { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private User(Guid id, string issuer, string subject, string? username, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(subject);
        Id = id;
        Issuer = issuer;
        Subject = subject;
        Username = username;
        CreatedAt = createdAt;
    }

    public static User Register(string issuer, string subject, string? username, DateTimeOffset now)
        => new(Guid.NewGuid(), DomainGuard.Text(issuer, nameof(Issuer), 512),
            DomainGuard.Text(subject, nameof(Subject), 512),
            username is { Length: > MaxUsernameLength } ? username[..MaxUsernameLength] : username, now);
}
