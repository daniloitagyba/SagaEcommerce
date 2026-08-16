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
    // each event's own OccurredAt/Version.
    //
    // Prefers @version (order_event_version_seq - see OrderStatusChanged's
    // own class comment) whenever either side of the comparison carries
    // one: a single cross-process Postgres sequence is immune to the clock
    // skew a wall-clock comparison is not. Before Milestone 91 this
    // compared decided_at alone - two pods' clocks even tens of
    // milliseconds apart was enough for a genuinely newer status carrying
    // a slightly earlier timestamp to be silently, permanently dropped,
    // with nothing logged and nothing alerted; see
    // docs/roadmap-milestones-91-99.md, "the read-model ordering guard
    // trusts physical clocks across services".
    //
    // Falls back to decided_at only when NEITHER side has a version -
    // PaymentDecided (choreography mode only; the deployed Orchestration
    // default never produces it) is the one remaining producer that
    // doesn't stamp one. A versioned write always beats a version-less
    // baseline (order_summaries.version IS NULL means either no write has
    // landed yet, or only PaymentDecided has); a version-less write can
    // never clobber a row a versioned write already reached - see the
    // second OR branch's absence of that case, which is deliberate, not an
    // oversight. `>=`, not `>`, on the decided_at fallback so a later write
    // for the exact same decided_at still applies rather than silently
    // losing to whichever happened to insert first.
    private const string UpsertDecisionSql = """
        INSERT INTO order_summaries (order_id, status, decided_at, projected_at, version)
        VALUES (@order_id, @status, @decided_at, @projected_at, @version)
        ON CONFLICT (order_id) DO UPDATE SET
            status = EXCLUDED.status,
            decided_at = EXCLUDED.decided_at,
            projected_at = EXCLUDED.projected_at,
            version = EXCLUDED.version
        WHERE
            (EXCLUDED.version IS NOT NULL
                AND (order_summaries.version IS NULL OR EXCLUDED.version > order_summaries.version))
            OR (
                EXCLUDED.version IS NULL AND order_summaries.version IS NULL
                AND (order_summaries.decided_at IS NULL OR EXCLUDED.decided_at >= order_summaries.decided_at)
            );
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

    /// <summary>
    /// version is trailing and optional (after cancellationToken, against
    /// the usual convention) specifically so PaymentDecided's existing call
    /// site - which has no version to give, see UpsertDecisionSql's own
    /// comment - keeps compiling unchanged. OrderStatusChanged's own call
    /// site (OrderProjectionProcessor.ProcessOrderStatusChangedAsync)
    /// always passes one.
    /// </summary>
    public Task ProjectPaymentDecidedAsync(
        Guid orderId,
        string status,
        DateTimeOffset decidedAt,
        DateTimeOffset projectedAt,
        CancellationToken cancellationToken,
        long? version = null)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            await using var command = dataSource.CreateCommand(UpsertDecisionSql);
            command.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
            command.Parameters.AddWithValue("status", NpgsqlDbType.Varchar, status);
            command.Parameters.AddWithValue("decided_at", NpgsqlDbType.TimestampTz, decidedAt);
            command.Parameters.AddWithValue("projected_at", NpgsqlDbType.TimestampTz, projectedAt);
            command.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, (object?)version ?? DBNull.Value);

            await command.ExecuteNonQueryAsync(ct);
        }, cancellationToken).AsTask();
    }
}
