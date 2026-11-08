using System.ComponentModel.DataAnnotations;
using Npgsql;

namespace CloudSwitch.Api.Infrastructure;

public sealed class DatabaseOptions
{
    [Required]
    public string Postgres { get; init; } = "";

    internal bool HasValidConnectionString()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(Postgres)
                && !string.IsNullOrWhiteSpace(new NpgsqlConnectionStringBuilder(Postgres).Host);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
