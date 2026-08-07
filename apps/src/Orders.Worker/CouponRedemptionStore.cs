using BuildingBlocks;
using Npgsql;
using NpgsqlTypes;
using Polly;
using Polly.Registry;

namespace Orders.Worker;

/// <summary>
/// Milestone 67: settles a coupon redemption once its order reaches a
/// terminal state. Without release, every declined payment would burn a
/// redemption permanently - a 100-use coupon exhausted by 100 failed
/// checkouts, no sale. Raw Npgsql, not EF, matching OrderStatusStore /
/// SagaOrchestrationStore: EF owns the schema, not the worker's hot paths.
/// </summary>
public sealed class CouponRedemptionStore(
    NpgsqlDataSource dataSource,
    ResiliencePipelineProvider<string> pipelineProvider,
    ILogger<CouponRedemptionStore> logger)
{
    // Guarded on state = 'Reserved', so a redelivered message or a second saga path is a no-op, not a double count.
    private const string ConfirmSql = """
        UPDATE coupon_redemptions
        SET state = @confirmed_state, settled_at = @settled_at
        WHERE code = @code AND order_id = @order_id AND state = @reserved_state;
        """;

    // Milestone 69: releasable from Confirmed as well as Reserved, since
    // fulfilment states let an order be confirmed and later cancelled - the
    // Milestone 67 Reserved-only guard silently did nothing in that case,
    // leaving the slot spent for an order that no longer exists. Released
    // is still excluded, so a double release can't hand the slot back twice.
    private const string ReleaseSql = """
        WITH released AS (
            UPDATE coupon_redemptions
            SET state = @released_state, settled_at = @settled_at
            WHERE code = @code AND order_id = @order_id
              AND state IN (@reserved_state, @confirmed_state)
            RETURNING code
        )
        UPDATE coupons
        SET redemption_count = GREATEST(redemption_count - 1, 0)
        FROM released
        WHERE coupons.code = released.code;
        """;

    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresPipeline);

    public async Task<bool> TryConfirmAsync(string code, Guid orderId, CancellationToken cancellationToken)
    {
        var settled = await ExecuteAsync(ConfirmSql, code, orderId, CouponRedemptionState.Confirmed, cancellationToken);
        if (settled)
        {
            CouponRedemptionLog.Confirmed(logger, code, orderId);
        }

        return settled;
    }

    public async Task<bool> TryReleaseAsync(string code, Guid orderId, CancellationToken cancellationToken)
    {
        var settled = await ExecuteAsync(ReleaseSql, code, orderId, CouponRedemptionState.Released, cancellationToken);
        if (settled)
        {
            CouponRedemptionLog.Released(logger, code, orderId);
        }

        return settled;
    }

    private async Task<bool> ExecuteAsync(
        string sql,
        string code,
        Guid orderId,
        string targetState,
        CancellationToken cancellationToken)
    {
        return await _pipeline.ExecuteAsync(async ct =>
        {
            await using var command = dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, code);
            command.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
            command.Parameters.AddWithValue("reserved_state", NpgsqlDbType.Varchar, CouponRedemptionState.Reserved);
            command.Parameters.AddWithValue("settled_at", NpgsqlDbType.TimestampTz, DateTimeOffset.UtcNow);

            command.Parameters.AddWithValue("confirmed_state", NpgsqlDbType.Varchar, CouponRedemptionState.Confirmed);
            if (targetState == CouponRedemptionState.Released)
            {
                command.Parameters.AddWithValue("released_state", NpgsqlDbType.Varchar, CouponRedemptionState.Released);
            }

            return await command.ExecuteNonQueryAsync(ct) > 0;
        }, cancellationToken);
    }
}

public sealed partial class CouponRedemptionLog
{
    [LoggerMessage(EventId = 9100, Level = LogLevel.Information, Message = "Confirmed redemption of coupon {Code} for order {OrderId}")]
    public static partial void Confirmed(ILogger logger, string code, Guid orderId);

    [LoggerMessage(EventId = 9101, Level = LogLevel.Information, Message = "Released redemption of coupon {Code} for order {OrderId} - the slot is back in the pool")]
    public static partial void Released(ILogger logger, string code, Guid orderId);
}
