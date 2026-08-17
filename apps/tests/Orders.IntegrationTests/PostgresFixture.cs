using System.Text.RegularExpressions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Orders.IntegrationTests;

/// <summary>One PostgreSQL container shared by every test class in this project via PostgresCollectionDefinition; isolation comes from a unique schema per test-class instance. PaymentPrimaryMigrationTests is the one documented exception, needing a whole database of its own.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private static readonly Regex ValidSchemaName = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("orders_test")
        .WithUsername("test_user")
        .WithPassword("test-password-not-a-secret")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Creates a fresh, uniquely-named schema on the shared container and returns a connection string scoped to it.</summary>
    public async Task<string> CreateSchemaAsync(string namePrefix)
    {
        var normalizedPrefix = namePrefix.ToLowerInvariant();
        if (!ValidSchemaName.IsMatch(normalizedPrefix))
        {
            throw new ArgumentException($"'{namePrefix}' is not a safe schema name prefix - expected lowercase letters, digits and underscores only.", nameof(namePrefix));
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var normalized = $"{normalizedPrefix}_{suffix}";

        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{normalized}\"";
        await command.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            SearchPath = $"{normalized},public"
        };
        return builder.ConnectionString;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollectionDefinition : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
