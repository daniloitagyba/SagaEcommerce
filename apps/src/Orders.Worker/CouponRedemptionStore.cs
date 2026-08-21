using BuildingBlocks;
using Npgsql;
using NpgsqlTypes;

namespace Orders.Worker;

/// <summary>Settles a coupon redemption once its order reaches a terminal state.</summary>
public sealed class CouponRedemptionStore
{
    private const string ConfirmSql = """
        UPDATE coupon_redemptions
        SET state = @confirmed_state, settled_at = @settled_at
        WHERE code = @code AND order_id = @order_id AND state = @reserved_state;
        """;

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

    public Task<bool> TryConfirmAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string code,
        Guid orderId,
        DateTimeOffset settledAt,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, ConfirmSql, code, orderId, settledAt, false, cancellationToken);

    public Task<bool> TryReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string code,
        Guid orderId,
        DateTimeOffset settledAt,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, ReleaseSql, code, orderId, settledAt, true, cancellationToken);

    private static async Task<bool> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        string code,
        Guid orderId,
        DateTimeOffset settledAt,
        bool isRelease,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, code);
        command.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        command.Parameters.AddWithValue("reserved_state", NpgsqlDbType.Varchar, CouponRedemptionState.Reserved);
        command.Parameters.AddWithValue("settled_at", NpgsqlDbType.TimestampTz, settledAt);
        command.Parameters.AddWithValue("confirmed_state", NpgsqlDbType.Varchar, CouponRedemptionState.Confirmed);
        if (isRelease)
        {
            command.Parameters.AddWithValue("released_state", NpgsqlDbType.Varchar, CouponRedemptionState.Released);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
