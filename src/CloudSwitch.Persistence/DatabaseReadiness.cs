using Npgsql;

namespace CloudSwitch.Persistence;

public sealed class DatabaseReadiness(NpgsqlDataSource dataSource)
{
    public async Task<bool> HasSchemaAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT to_regclass('public."Jobs"') IS NOT NULL
                AND to_regclass('public."OutboxMessage"') IS NOT NULL
                AND to_regclass('public."Users"') IS NOT NULL
            """);

        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }
}
