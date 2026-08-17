using BuildingBlocks;
using Npgsql;
using NpgsqlTypes;

namespace Orders.Worker;

/// <summary>
/// Settles promotion campaign claims.
/// </summary>
public sealed class PromotionCampaignStore
{
    private const string ConfirmSql = """
        UPDATE promotion_campaign_claims
        SET state = @confirmed_state, settled_at = @settled_at
        WHERE code = @code AND order_id = @order_id AND state = @reserved_state;
        """;

    private const string ReleaseSql = """
        WITH released AS (
            UPDATE promotion_campaign_claims
            SET state = @released_state, settled_at = @settled_at
            WHERE code = @code AND order_id = @order_id
              AND state IN (@reserved_state, @confirmed_state)
            RETURNING code, amount
        )
        UPDATE promotion_campaigns
        SET budget_remaining = LEAST(budget_remaining + released.amount, total_budget)
        FROM released
        WHERE promotion_campaigns.code = released.code;
        """;

    public Task<bool> TryConfirmAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string code,
        Guid orderId,
        DateTimeOffset settledAt,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, ConfirmSql, code, orderId, settledAt, isRelease: false, cancellationToken);

    public Task<bool> TryReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string code,
        Guid orderId,
        DateTimeOffset settledAt,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, ReleaseSql, code, orderId, settledAt, isRelease: true, cancellationToken);

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
        command.Parameters.AddWithValue("reserved_state", NpgsqlDbType.Varchar, PromotionCampaignClaimState.Reserved);
        command.Parameters.AddWithValue("settled_at", NpgsqlDbType.TimestampTz, settledAt);
        command.Parameters.AddWithValue("confirmed_state", NpgsqlDbType.Varchar, PromotionCampaignClaimState.Confirmed);
        if (isRelease)
        {
            command.Parameters.AddWithValue("released_state", NpgsqlDbType.Varchar, PromotionCampaignClaimState.Released);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
