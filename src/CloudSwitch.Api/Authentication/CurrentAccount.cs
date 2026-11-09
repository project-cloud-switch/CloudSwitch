using CloudSwitch.Persistence.Domain;
using CloudSwitch.Persistence.Repositories;

namespace CloudSwitch.Api.Authentication;

public sealed record Account(Guid Id, string Issuer, string Subject, string? Username);

public interface ICurrentAccount
{
    Task<Account> GetAsync(CancellationToken ct);
}

internal sealed class CurrentAccount(ICurrentUser currentUser, UserRepository users) : ICurrentAccount
{
    public async Task<Account> GetAsync(CancellationToken ct)
    {
        var identity = currentUser.Identity ?? throw new InvalidOperationException("An authenticated identity is required.");
        var user = await users.GetOrCreateAsync(
            User.Register(identity.Issuer, identity.Subject, identity.Username, DateTimeOffset.UtcNow), ct);

        return new Account(user.Id, user.Issuer, user.Subject, user.Username);
    }
}
