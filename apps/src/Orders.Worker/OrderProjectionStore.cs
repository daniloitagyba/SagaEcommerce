using BuildingBlocks;
using Npgsql;
using NpgsqlTypes;
using Polly;
using Polly.Registry;

namespace Orders.Worker;

public sealed class OrderProjectionStore(NpgsqlDataSource dataSource, ResiliencePipelineProvider<string> pipelineProvider)
{
    private const string UpsertCreatedSql = """
        INSERT INTO order_summaries (order_id, customer_id, amount, currency, status, order_created_at, projected_at)
        VALUES (@order_id, @customer_id, @amount, @currency, 'Created', @order_created_at, @projected_at)
        ON CONFLICT (order_id) DO UPDATE SET
            customer_id = EXCLUDED.customer_id,
            amount = EXCLUDED.amount,
            currency = EXCLUDED.currency,
            order_created_at = EXCLUDED.order_created_at,
            projected_at = EXCLUDED.projected_at;
        """;

    // The WHERE clause is the ordering guard: PaymentDecided,
    // OrderStatusChanged (saga-driven and API-driven) and inbox-deduplicated
    // redeliveries can all reach this upsert for the same order from
    // different topics/partitions, with no ordering between them beyond
    // each event's own OccurredAt. Without this guard, a transient publish
    // failure that pushes one event's outbox retry behind a later one's
    // (see OutboxPublisher/BuildingBlocks.Persistence) is enough for an
    // older status (e.g. Shipped, redelivered late) to permanently
    // overwrite a newer one (Delivered) that already projected. `>=`, not
    // `>`, so a later write for the exact same decided_at (a status derived
    // and re-emitted from the same instant) still applies rather than
    // silently losing to whichever happened to insert first.
    private const string UpsertDecisionSql = """
        INSERT INTO order_summaries (order_id, status, decided_at, projected_at)
        VALUES (@order_id, @status, @decided_at, @projected_at)
        ON CONFLICT (order_id) DO UPDATE SET
            status = EXCLUDED.status,
            decided_at = EXCLUDED.decided_at,
            projected_at = EXCLUDED.projected_at
        WHERE order_summaries.decided_at IS NULL OR EXCLUDED.decided_at >= order_summaries.decided_at;
        """;

    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresPipeline);

    public Task ProjectOrderCreatedAsync(
        Guid orderId,
        string customerId,
        decimal amount,
        string currency,
        DateTimeOffset orderCreatedAt,
        DateTimeOffset projectedAt,
        CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            await using var command = dataSource.CreateCommand(UpsertCreatedSql);
            command.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
            command.Parameters.AddWithValue("customer_id", NpgsqlDbType.Varchar, customerId);
            command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, amount);
            command.Parameters.AddWithValue("currency", NpgsqlDbType.Varchar, currency);
            command.Parameters.AddWithValue("order_created_at", NpgsqlDbType.TimestampTz, orderCreatedAt);
            command.Parameters.AddWithValue("projected_at", NpgsqlDbType.TimestampTz, projectedAt);

            await command.ExecuteNonQueryAsync(ct);
        }, cancellationToken).AsTask();
    }

    public Task ProjectPaymentDecidedAsync(
        Guid orderId,
        string status,
        DateTimeOffset decidedAt,
        DateTimeOffset projectedAt,
        CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            await using var command = dataSource.CreateCommand(UpsertDecisionSql);
            command.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
            command.Parameters.AddWithValue("status", NpgsqlDbType.Varchar, status);
            command.Parameters.AddWithValue("decided_at", NpgsqlDbType.TimestampTz, decidedAt);
            command.Parameters.AddWithValue("projected_at", NpgsqlDbType.TimestampTz, projectedAt);

            await command.ExecuteNonQueryAsync(ct);
        }, cancellationToken).AsTask();
    }
}
