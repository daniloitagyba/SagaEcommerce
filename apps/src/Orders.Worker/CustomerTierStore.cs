using Npgsql;
using NpgsqlTypes;

namespace Orders.Worker;

/// <summary>
/// Records a completed order and lets standing climb. Runs
/// on <em>confirmation</em>, not creation, or placing and cancelling would
/// be the cheapest route to Gold. Raw Npgsql, matching this worker's other writes.
/// </summary>
public sealed class CustomerTierStore
{
    // One statement: increment and re-derive tier together, or two concurrent confirmations could each miss the other's contribution.
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

    public async Task RecordCompletedOrderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
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
        command.CommandText = RecordSql;
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
/// Mirrors Orders.Domain.CustomerTiers - Orders.Worker deliberately doesn't
/// reference Orders.Domain, so these are duplicated with a test pinning the two together.
/// </summary>
public static class CustomerTierThresholds
{
    public const decimal Silver = 1_000m;
    public const decimal Gold = 5_000m;
    public const string BronzeName = "Bronze";
    public const string SilverName = "Silver";
    public const string GoldName = "Gold";
}
