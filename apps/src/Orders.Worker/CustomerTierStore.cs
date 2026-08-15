using Npgsql;
using NpgsqlTypes;

namespace Orders.Worker;

/// <summary>
/// Records a completed order and lets standing climb, and reverses that
/// contribution if the order is later cancelled or fully returned. Records
/// on <em>confirmation</em>, not creation, or placing and cancelling would
/// have been the cheapest route to Gold on its own - but recording early
/// was not, by itself, enough: confirm-then-cancel still credited spend
/// permanently until ReverseCompletedOrderAsync closed that gap too (see
/// OrderStatusStore.ApplySideEffectsAsync's own comment on the Cancelled
/// branch for the state-graph reasoning). Raw Npgsql, matching this
/// worker's other writes.
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

    // Mirrors Orders.Domain.Customer.ReverseCompletedOrder: subtracts spend
    // and floors at zero the same way CouponRedemptionStore's release does
    // for redemption_count, but deliberately does NOT re-derive tier -
    // taking a discount away retroactively generates support tickets; real
    // loyalty programmes review downward on a schedule, not on the instant
    // a refund posts. A customer keeps whatever standing a since-reversed
    // order already earned them.
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
    /// Reverses a completed order's contribution to standing - a
    /// cancellation of an already-Confirmed order, or a full return, must
    /// not leave the customer permanently credited for spend that was
    /// given back. A <em>partial</em> return does not call this: the
    /// customer kept most of the order, and there is no policy yet for
    /// pro-rating standing down for a partial refund.
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
