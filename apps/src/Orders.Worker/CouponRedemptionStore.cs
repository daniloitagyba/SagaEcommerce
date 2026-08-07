using BuildingBlocks;
using Npgsql;
using NpgsqlTypes;
using Polly;
using Polly.Registry;

namespace Orders.Worker;

/// <summary>
/// Milestone 67: settles a coupon redemption once the order it belongs to
/// reaches a terminal state.
///
/// Reserving a slot at checkout and never giving it back would make every
/// declined payment burn a redemption permanently - a coupon limited to
/// 100 uses would be exhausted by 100 failed checkouts without a single
/// sale. Release is what makes the redemption a saga participant rather
/// than a fire-and-forget increment, and it is the mirror image of
/// Inventory's reservation release.
///
/// Raw Npgsql rather than EF, matching OrderStatusStore/SagaOrchestrationStore
/// in this project: EF owns the schema (see Orders.Domain.Coupon and the
/// AddCouponLifecycle migration), the worker's hot paths do not go through it.
/// </summary>
public sealed class CouponRedemptionStore(
    NpgsqlDataSource dataSource,
    ResiliencePipelineProvider<string> pipelineProvider,
    ILogger<CouponRedemptionStore> logger)
{
    // Both statements are guarded on state = 'Reserved', so a redelivered
    // message or a second saga path settling the same order is a no-op
    // rather than a double count. The decrement rides on the same
    // condition, which is what stops a released slot being handed back
    // twice.
    private const string ConfirmSql = """
        UPDATE coupon_redemptions
        SET state = @confirmed_state, settled_at = @settled_at
        WHERE code = @code AND order_id = @order_id AND state = @reserved_state;
        """;

    // Milestone 69: releasable from Confirmed as well as Reserved.
    //
    // Milestone 67 guarded this on Reserved alone, which was correct while
    // an order settled exactly once - it went Created -> Confirmed or
    // Created -> Cancelled, never both. The fulfilment states break that
    // assumption: an order can now be confirmed and cancelled afterwards,
    // and the redemption would already have moved to Confirmed by then, so
    // the release silently did nothing and the slot stayed spent for an
    // order that no longer exists.
    //
    // Released is still excluded, which is what keeps a double release from
    // handing the same slot back twice - redemption_count is incremented
    // once at reservation and never touched by Confirmed, so exactly one
    // decrement is owed regardless of which state it is released from.
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
