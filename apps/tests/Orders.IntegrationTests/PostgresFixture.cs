using System.Text.RegularExpressions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Orders.IntegrationTests;

/// <summary>
/// One PostgreSQL container shared by every test class in this project via
/// PostgresCollectionDefinition, instead of the 17 each test class used to start (and
/// migrate) on its own. Isolation moves from "own container" to "own
/// schema" - CreateSchemaAsync hands each caller a connection string whose
/// Npgsql SearchPath already points only at a schema unique to that call,
/// so unqualified SQL (EF's own generated SQL and the raw ADO.NET a lot of
/// these test classes also issue directly) resolves into that schema
/// without either style of query needing to change at all. The name passed
/// in gets a Guid suffix appended before it becomes the schema name -
/// xUnit gives every [Fact] its own fresh instance of the test class, and
/// each of those instances calls this once from InitializeAsync, so a
/// class with several [Fact]s needs one fresh, empty schema per instance,
/// the same isolation each one got from its own dedicated container
/// before this change. PaymentPrimaryMigrationTests is the one documented
/// exception - it exercises a specific migration by name, which needs a
/// database no other test has touched, not just a schema no other test has
/// touched.
/// </summary>
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

    /// <summary>
    /// Creates a fresh schema on the shared container - named after the
    /// caller plus a unique suffix, so repeated calls with the same name
    /// never collide - and returns a connection string scoped to it.
    /// Callers still run their own EF migrations (or raw DDL) against the
    /// returned string exactly as they did against a dedicated container's
    /// connection string before - only where that string comes from has
    /// changed.
    /// </summary>
    public async Task<string> CreateSchemaAsync(string namePrefix)
    {
        var normalizedPrefix = namePrefix.ToLowerInvariant();
        if (!ValidSchemaName.IsMatch(normalizedPrefix))
        {
            throw new ArgumentException($"'{namePrefix}' is not a safe schema name prefix - expected lowercase letters, digits and underscores only.", nameof(namePrefix));
        }

        // Postgres identifiers silently truncate past 63 bytes - an 8-hex-char
        // suffix (32 bits, plenty for test-schema uniqueness at this volume)
        // keeps prefix + suffix well under that even for the longest class
        // name in this project.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var normalized = $"{normalizedPrefix}_{suffix}";

        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // Identifier, not a value - cannot be parameterized, hence the
        // upfront ValidSchemaName check above rather than escaping.
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
