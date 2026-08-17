using Npgsql;
using NpgsqlTypes;

namespace Orders.Worker;

/// <summary>
/// Maintains customer tiers from completed orders.
/// </summary>
public sealed class CustomerTierStore
{
    private const string RecordSql = """
        UPDATE customers
        SET lifetime_spend = lifetime_spend + @amount,
            completed_order_count = completed_order_count + 1,
            tier = CASE
                WHEN lifetime_spend + @amount >= @gold_threshold THEN @gold
                WHEN lifetime_spend + @amount >= @silver_threshold THEN @silver
                ELSE @bronze
            END
        WHERE id = @id;
        """;

    private const string ReverseSql = """
        UPDATE customers
        SET lifetime_spend = GREATEST(lifetime_spend - @amount, 0),
            completed_order_count = GREATEST(completed_order_count - 1, 0)
        WHERE id = @id;
        """;

    public Task RecordCompletedOrderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string customerId,
        decimal amount,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, RecordSql, customerId, amount, cancellationToken);

    /// <summary>
    /// Reverses a completed order's contribution to standing.
    /// </summary>
    public Task ReverseCompletedOrderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string customerId,
        decimal amount,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, ReverseSql, customerId, amount, cancellationToken);

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        string customerId,
        decimal amount,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, customerId);
        command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, amount);
        command.Parameters.AddWithValue("gold_threshold", NpgsqlDbType.Numeric, CustomerTierThresholds.Gold);
        command.Parameters.AddWithValue("silver_threshold", NpgsqlDbType.Numeric, CustomerTierThresholds.Silver);
        command.Parameters.AddWithValue("gold", NpgsqlDbType.Varchar, CustomerTierThresholds.GoldName);
        command.Parameters.AddWithValue("silver", NpgsqlDbType.Varchar, CustomerTierThresholds.SilverName);
        command.Parameters.AddWithValue("bronze", NpgsqlDbType.Varchar, CustomerTierThresholds.BronzeName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>
/// Mirrors Orders.Domain.CustomerTiers.
/// </summary>
public static class CustomerTierThresholds
{
    public const decimal Silver = 1_000m;
    public const decimal Gold = 5_000m;
    public const string BronzeName = "Bronze";
    public const string SilverName = "Silver";
    public const string GoldName = "Gold";
}
