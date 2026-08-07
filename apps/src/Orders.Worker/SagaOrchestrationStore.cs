using BuildingBlocks;
using Npgsql;
using NpgsqlTypes;
using Polly;
using Polly.Registry;

namespace Orders.Worker;

public sealed record SagaOrchestrationRecord(
    string CorrelationId,
    string CustomerId,
    string PaymentMethod,
    string ShippingPostalPrefix,
    DateTimeOffset RequestedAt,
    string Step,
    Guid ReservationId,
    string Sku,
    int Quantity,
    decimal Amount,
    string Currency);

/// <summary>
/// Milestone 36: Postgres-backed replacement for the in-memory
/// SagaOrchestrationTracker that didn't survive a pod restart. Raw Npgsql,
/// matching OrderEventStoreAppender/OrderProjectionStore - EF only owns
/// this table's schema. Milestone 43: TryAdvanceAsync is the
/// general-purpose "move to the next step" operation, gated on the row's
/// CURRENT step matching what the caller expects, so a stale or duplicate
/// reply for an already-passed step is a no-op rather than corrupting state.
/// </summary>
public sealed class SagaOrchestrationStore(NpgsqlDataSource dataSource, ResiliencePipelineProvider<string> pipelineProvider)
{
    private const string TrackReserveRequestedSql = """
        INSERT INTO saga_orchestration_states (order_id, correlation_id, customer_id, payment_method, shipping_postal_prefix, requested_at, step, reservation_id, sku, quantity, amount, currency)
        VALUES (@order_id, @correlation_id, @customer_id, @payment_method, @shipping_postal_prefix, @requested_at, @step, @reservation_id, @sku, @quantity, @amount, @currency)
        ON CONFLICT (order_id) DO NOTHING;
        """;

    private const string TryAdvanceSql = """
        UPDATE saga_orchestration_states
        SET step = @next_step, requested_at = @requested_at
        WHERE order_id = @order_id AND step = @expected_step
        RETURNING correlation_id, customer_id, payment_method, shipping_postal_prefix, requested_at, step, reservation_id, sku, quantity, amount, currency;
        """;

    private const string TryCompleteSql = """
        DELETE FROM saga_orchestration_states
        WHERE order_id = @order_id AND step = @expected_step
        RETURNING correlation_id, customer_id, payment_method, shipping_postal_prefix, requested_at, step, reservation_id, sku, quantity, amount, currency;
        """;

    // FOR UPDATE SKIP LOCKED here is belt-and-suspenders, not the primary
    // mechanism - leader election already ensures one active sweeper; this
    // guards the brief window during a leadership handoff.
    private const string ClaimTimedOutSql = """
        DELETE FROM saga_orchestration_states
        WHERE order_id IN (
            SELECT order_id
            FROM saga_orchestration_states
            WHERE requested_at <= @cutoff
            ORDER BY requested_at
            LIMIT @batch_size
            FOR UPDATE SKIP LOCKED
        )
        RETURNING order_id, correlation_id, customer_id, payment_method, shipping_postal_prefix, requested_at, step, reservation_id, sku, quantity, amount, currency;
        """;

    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresPipeline);

    public Task TrackReserveRequestedAsync(
        Guid orderId,
        string correlationId,
        string customerId,
        string paymentMethod,
        string shippingPostalPrefix,
        Guid reservationId,
        string sku,
        int quantity,
        decimal amount,
        string currency,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            await using var command = dataSource.CreateCommand(TrackReserveRequestedSql);
            command.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
            command.Parameters.AddWithValue("correlation_id", NpgsqlDbType.Varchar, correlationId);
            command.Parameters.AddWithValue("customer_id", NpgsqlDbType.Varchar, customerId);
            command.Parameters.AddWithValue("payment_method", NpgsqlDbType.Varchar, paymentMethod);
            command.Parameters.AddWithValue("shipping_postal_prefix", NpgsqlDbType.Varchar, shippingPostalPrefix);
            command.Parameters.AddWithValue("requested_at", NpgsqlDbType.TimestampTz, requestedAt);
            command.Parameters.AddWithValue("step", NpgsqlDbType.Varchar, SagaStep.ReserveInventory);
            command.Parameters.AddWithValue("reservation_id", NpgsqlDbType.Uuid, reservationId);
            command.Parameters.AddWithValue("sku", NpgsqlDbType.Varchar, sku);
            command.Parameters.AddWithValue("quantity", NpgsqlDbType.Integer, quantity);
            command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, amount);
            command.Parameters.AddWithValue("currency", NpgsqlDbType.Varchar, currency);
            await command.ExecuteNonQueryAsync(ct);
        }, cancellationToken).AsTask();
    }

    public Task<SagaOrchestrationRecord?> TryAdvanceAsync(
        Guid orderId,
        string expectedCurrentStep,
        string nextStep,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            await using var command = dataSource.CreateCommand(TryAdvanceSql);
            command.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
            command.Parameters.AddWithValue("expected_step", NpgsqlDbType.Varchar, expectedCurrentStep);
            command.Parameters.AddWithValue("next_step", NpgsqlDbType.Varchar, nextStep);
            command.Parameters.AddWithValue("requested_at", NpgsqlDbType.TimestampTz, requestedAt);
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? await ReadRecordAsync(reader, ct) : null;
        }, cancellationToken).AsTask();
    }

    public Task<SagaOrchestrationRecord?> TryCompleteAsync(
        Guid orderId,
        string expectedCurrentStep,
        CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            await using var command = dataSource.CreateCommand(TryCompleteSql);
            command.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
            command.Parameters.AddWithValue("expected_step", NpgsqlDbType.Varchar, expectedCurrentStep);
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? await ReadRecordAsync(reader, ct) : null;
        }, cancellationToken).AsTask();
    }

    public Task<IReadOnlyList<(Guid OrderId, SagaOrchestrationRecord Saga)>> ClaimTimedOutAsync(
        TimeSpan timeout,
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            await using var command = dataSource.CreateCommand(ClaimTimedOutSql);
            command.Parameters.AddWithValue("cutoff", NpgsqlDbType.TimestampTz, now - timeout);
            command.Parameters.AddWithValue("batch_size", NpgsqlDbType.Integer, batchSize);

            var claimed = new List<(Guid, SagaOrchestrationRecord)>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                claimed.Add((
                    reader.GetGuid(0),
                    new SagaOrchestrationRecord(
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        await reader.GetFieldValueAsync<DateTimeOffset>(5, ct),
                        reader.GetString(6),
                        reader.GetGuid(7),
                        reader.GetString(8),
                        reader.GetInt32(9),
                        reader.GetDecimal(10),
                        reader.GetString(11))));
            }

            return (IReadOnlyList<(Guid, SagaOrchestrationRecord)>)claimed;
        }, cancellationToken).AsTask();
    }

    private static async Task<SagaOrchestrationRecord> ReadRecordAsync(NpgsqlDataReader reader, CancellationToken cancellationToken)
    {
        return new SagaOrchestrationRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            await reader.GetFieldValueAsync<DateTimeOffset>(4, cancellationToken),
            reader.GetString(5),
            reader.GetGuid(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.GetDecimal(9),
            reader.GetString(10));
    }
}

/// <summary>
/// The four steps this saga walks through. Not an enum mapped by EF - the
/// column is a plain string so ad hoc SQL inspection during development
/// doesn't need to decode an integer.
/// </summary>
public static class SagaStep
{
    public const string ReserveInventory = "ReserveInventory";
    public const string DecidePayment = "DecidePayment";
    public const string CommitInventory = "CommitInventory";
    public const string ReleaseInventory = "ReleaseInventory";
}
