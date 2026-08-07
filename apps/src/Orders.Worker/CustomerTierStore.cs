using BuildingBlocks;
using Npgsql;
using NpgsqlTypes;
using Polly;
using Polly.Registry;

namespace Orders.Worker;

/// <summary>
/// Milestone 71: records a completed order against the customer and lets
/// their standing climb.
///
/// Runs on <em>confirmation</em>, not on order creation - otherwise placing
/// and cancelling orders would be the cheapest possible way to reach Gold
/// and earn a permanent discount for nothing. Raw Npgsql, matching every
/// other write this worker does against the orders database.
/// </summary>
public sealed class CustomerTierStore(
    NpgsqlDataSource dataSource,
    ResiliencePipelineProvider<string> pipelineProvider,
    ILogger<CustomerTierStore> logger)
{
    // One statement: increment the totals and re-derive the tier from the
    // new lifetime spend in the same UPDATE. Reading, computing in C# and
    // writing back would let two concurrent confirmations each miss the
    // other's contribution.
    private const string RecordSql = """
        UPDATE customers
        SET lifetime_spend = lifetime_spend + @amount,
            completed_order_count = completed_order_count + 1,
            tier = CASE
                WHEN lifetime_spend + @amount >= @gold_threshold THEN @gold
                WHEN lifetime_spend + @amount >= @silver_threshold THEN @silver
                ELSE @bronze
            END
        WHERE id = @id
        RETURNING tier;
        """;

    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresPipeline);

    public async Task RecordCompletedOrderAsync(
        string customerId,
        decimal amount,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return;
        }

        var tier = await _pipeline.ExecuteAsync(async ct =>
        {
            await using var command = dataSource.CreateCommand(RecordSql);
            command.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, customerId);
            command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, amount);
            command.Parameters.AddWithValue("gold_threshold", NpgsqlDbType.Numeric, CustomerTierThresholds.Gold);
            command.Parameters.AddWithValue("silver_threshold", NpgsqlDbType.Numeric, CustomerTierThresholds.Silver);
            command.Parameters.AddWithValue("gold", NpgsqlDbType.Varchar, CustomerTierThresholds.GoldName);
            command.Parameters.AddWithValue("silver", NpgsqlDbType.Varchar, CustomerTierThresholds.SilverName);
            command.Parameters.AddWithValue("bronze", NpgsqlDbType.Varchar, CustomerTierThresholds.BronzeName);

            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? reader.GetString(0) : null;
        }, cancellationToken);

        if (tier is not null)
        {
            CustomerTierLog.Recorded(logger, customerId, amount, tier);
        }
    }
}

/// <summary>
/// Mirrors Orders.Domain.CustomerTiers. Orders.Worker deliberately does not
/// reference Orders.Domain (see CouponRedemptionState for the same
/// reasoning), and the thresholds have to live somewhere this SQL can read
/// them - so they are duplicated here with a test pinning the two together.
/// </summary>
public static class CustomerTierThresholds
{
    public const decimal Silver = 1_000m;
    public const decimal Gold = 5_000m;
    public const string BronzeName = "Bronze";
    public const string SilverName = "Silver";
    public const string GoldName = "Gold";
}

public sealed partial class CustomerTierLog
{
    [LoggerMessage(EventId = 9300, Level = LogLevel.Information, Message = "Recorded {Amount} for customer {CustomerId}; standing is now {Tier}")]
    public static partial void Recorded(ILogger logger, string customerId, decimal amount, string tier);
}
