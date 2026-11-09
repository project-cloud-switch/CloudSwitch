using Microsoft.EntityFrameworkCore;
using CloudSwitch.Persistence.Domain;

namespace CloudSwitch.Persistence.Repositories;

public sealed class UserRepository(CloudSwitchDbContext db)
{
    public async Task<User> GetOrCreateAsync(User user, CancellationToken cancellationToken)
        => (await db.Users.FromSqlInterpolated($"""
            INSERT INTO "Users" ("Id", "Issuer", "Subject", "Username", "CreatedAt")
            VALUES ({user.Id}, {user.Issuer}, {user.Subject}, {user.Username}, {user.CreatedAt})
            ON CONFLICT ("Issuer", "Subject") DO UPDATE SET "Username" = EXCLUDED."Username"
            RETURNING *
            """).AsNoTracking().ToListAsync(cancellationToken)).Single();
}
