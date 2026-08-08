using BuildingBlocks;
using Npgsql;
using NpgsqlTypes;
using Polly;
using Polly.Registry;

namespace Orders.Worker;

public sealed record SagaLineRecord(
    int LineIndex,
    Guid ReservationId,
    string Sku,
    int Quantity,
    bool? Reserved,
    bool? Committed,
    bool? Released);

public sealed record SagaOrchestrationRecord(
    string CorrelationId,
    string CustomerId,
    string PaymentMethod,
    string ShippingPostalPrefix,
    DateTimeOffset RequestedAt,
    string Step,
    decimal Amount,
    string Currency,
    IReadOnlyList<SagaLineRecord> Lines);

public sealed record SagaReservationLine(Guid ReservationId, string Sku, int Quantity);

/// <summary>
/// Milestone 36: Postgres-backed replacement for the in-memory
/// SagaOrchestrationTracker that didn't survive a pod restart. Raw Npgsql,
/// matching OrderEventStoreAppender/OrderProjectionStore - EF only owns
/// this table's schema. Milestone 43: TryAdvanceAsync is the
/// general-purpose "move to the next step" operation, gated on the row's
/// CURRENT step matching what the caller expects, so a stale or duplicate
/// reply for an already-passed step is a no-op rather than corrupting state.
///
/// Milestone 78: a saga now tracks every line of the order, not just the
/// largest by value - <see cref="SagaOrchestrationRecord.Lines"/> is one
/// row per SKU in `saga_orchestration_lines`, each with its own
/// ReservationId and its own Reserved/Committed/Released outcome. The
/// parent row's Step still drives the order-level 4-step state machine
/// unchanged (that's what Milestone 56's TLA+ model and Milestone 58's
/// deterministic simulation both verify, and neither needed to change for
/// this milestone - they model "the reservation step succeeded" as one
/// abstract event, never how many concrete reservations that required).
/// What's new is that each of those four steps now waits for every line's
/// reply, not one, before the parent row is allowed to advance -
/// RecordLineOutcomeAsync records one line's answer, and the caller
/// re-reads every line via the returned snapshot to decide whether all of
/// them have now answered.
/// </summary>
public sealed class SagaOrchestrationStore(NpgsqlDataSource dataSource, ResiliencePipelineProvider<string> pipelineProvider)
{
    private const string InsertParentSql = """
        INSERT INTO saga_orchestration_states (order_id, correlation_id, customer_id, payment_method, shipping_postal_prefix, requested_at, step, amount, currency)
        VALUES (@order_id, @correlation_id, @customer_id, @payment_method, @shipping_postal_prefix, @requested_at, @step, @amount, @currency)
        ON CONFLICT (order_id) DO NOTHING;
        """;

    private const string InsertLineSql = """
        INSERT INTO saga_orchestration_lines (order_id, line_index, reservation_id, sku, quantity)
        VALUES (@order_id, @line_index, @reservation_id, @sku, @quantity)
        ON CONFLICT (order_id, line_index) DO NOTHING;
        """;

    private const string TryAdvanceSql = """
        UPDATE saga_orchestration_states
        SET step = @next_step, requested_at = @requested_at
        WHERE order_id = @order_id AND step = @expected_step
        RETURNING correlation_id, customer_id, payment_method, shipping_postal_prefix, requested_at, step, amount, currency;
        """;

    private const string TryCompleteSql = """
        DELETE FROM saga_orchestration_states
        WHERE order_id = @order_id AND step = @expected_step
        RETURNING correlation_id, customer_id, payment_method, shipping_postal_prefix, requested_at, step, amount, currency;
        """;

    // FOR UPDATE SKIP LOCKED here is belt-and-suspenders, not the primary
    // mechanism - leader election already ensures one active sweeper; this
    // guards the brief window during a leadership handoff.
    private const string ClaimCandidatesSql = """
        SELECT order_id
        FROM saga_orchestration_states
        WHERE requested_at <= @cutoff
        ORDER BY requested_at
        LIMIT @batch_size
        FOR UPDATE SKIP LOCKED;
        """;

    private const string SelectParentByIdsSql = """
        SELECT order_id, correlation_id, customer_id, payment_method, shipping_postal_prefix, requested_at, step, amount, currency
        FROM saga_orchestration_states
        WHERE order_id = ANY(@order_ids);
        """;

    private const string DeleteByIdsSql = """
        DELETE FROM saga_orchestration_states WHERE order_id = ANY(@order_ids);
        """;

    private const string SelectLinesSql = """
        SELECT line_index, reservation_id, sku, quantity, reserved, committed, released
        FROM saga_orchestration_lines
        WHERE order_id = @order_id
        ORDER BY line_index;
        """;

    private const string SelectLinesForOrdersSql = """
        SELECT order_id, line_index, reservation_id, sku, quantity, reserved, committed, released
        FROM saga_orchestration_lines
        WHERE order_id = ANY(@order_ids)
        ORDER BY order_id, line_index;
        """;

    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresPipeline);

    public Task TrackReserveRequestedAsync(
        Guid orderId,
        string correlationId,
        string customerId,
        string paymentMethod,
        string shippingPostalPrefix,
        IReadOnlyList<SagaReservationLine> lines,
        decimal amount,
        string currency,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            await using (var command = new NpgsqlCommand(InsertParentSql, connection, transaction))
            {
                command.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
                command.Parameters.AddWithValue("correlation_id", NpgsqlDbType.Varchar, correlationId);
                command.Parameters.AddWithValue("customer_id", NpgsqlDbType.Varchar, customerId);
                command.Parameters.AddWithValue("payment_method", NpgsqlDbType.Varchar, paymentMethod);
                command.Parameters.AddWithValue("shipping_postal_prefix", NpgsqlDbType.Varchar, shippingPostalPrefix);
                command.Parameters.AddWithValue("requested_at", NpgsqlDbType.TimestampTz, requestedAt);
                command.Parameters.AddWithValue("step", NpgsqlDbType.Varchar, SagaStep.ReserveInventory);
                command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, amount);
                command.Parameters.AddWithValue("currency", NpgsqlDbType.Varchar, currency);
                await command.ExecuteNonQueryAsync(ct);
            }

            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                await using var command = new NpgsqlCommand(InsertLineSql, connection, transaction);
                command.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
                command.Parameters.AddWithValue("line_index", NpgsqlDbType.Integer, index);
                command.Parameters.AddWithValue("reservation_id", NpgsqlDbType.Uuid, line.ReservationId);
                command.Parameters.AddWithValue("sku", NpgsqlDbType.Varchar, line.Sku);
                command.Parameters.AddWithValue("quantity", NpgsqlDbType.Integer, line.Quantity);
                await command.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
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
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            var parent = await ReadParentAsync(reader, ct);
            await reader.CloseAsync();
            var lines = await SelectLinesAsync(orderId, ct);
            return parent with { Lines = lines };
        }, cancellationToken).AsTask();
    }

    /// <summary>
    /// Deletes the parent row (and, via ON DELETE CASCADE, its lines) only
    /// if it's still on <paramref name="expectedCurrentStep"/>, returning a
    /// snapshot of the lines as they stood immediately before deletion -
    /// callers use that snapshot (e.g. "which lines were actually
    /// Reserved") to decide what compensation, if any, still needs
    /// publishing after this call.
    /// </summary>
    public Task<SagaOrchestrationRecord?> TryCompleteAsync(
        Guid orderId,
        string expectedCurrentStep,
        CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            var lines = await SelectLinesAsync(connection, transaction, orderId, ct);

            SagaOrchestrationRecord? parent = null;
            await using (var command = new NpgsqlCommand(TryCompleteSql, connection, transaction))
            {
                command.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
                command.Parameters.AddWithValue("expected_step", NpgsqlDbType.Varchar, expectedCurrentStep);
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    parent = await ReadParentAsync(reader, ct);
                }
            }

            await transaction.CommitAsync(ct);
            return parent is null ? null : parent with { Lines = lines };
        }, cancellationToken).AsTask();
    }

    /// <summary>
    /// Records one line's outcome for whichever field the caller names
    /// (Reserved/Committed/Released), guarded by "the field is still
    /// unset" the same way TryAdvanceAsync guards on Step - a redelivered
    /// reply for a line that already has an answer, or a late reply for an
    /// order whose saga row (and so whose line rows, via cascade) is
    /// already gone, is a no-op. Returns the full up-to-date line list for
    /// the order so the caller can decide whether every line has now
    /// answered - or null if the order/line no longer exists at all.
    /// </summary>
    public async Task<IReadOnlyList<SagaLineRecord>?> RecordLineOutcomeAsync(
        Guid orderId,
        Guid reservationId,
        SagaLineOutcomeField field,
        bool value,
        CancellationToken cancellationToken)
    {
        var column = field switch
        {
            SagaLineOutcomeField.Reserved => "reserved",
            SagaLineOutcomeField.Committed => "committed",
            SagaLineOutcomeField.Released => "released",
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        var updateSql = $"""
            UPDATE saga_orchestration_lines
            SET {column} = @value
            WHERE order_id = @order_id AND reservation_id = @reservation_id AND {column} IS NULL;
            """;

        await _pipeline.ExecuteAsync(async ct =>
        {
            await using var command = dataSource.CreateCommand(updateSql);
            command.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
            command.Parameters.AddWithValue("reservation_id", NpgsqlDbType.Uuid, reservationId);
            command.Parameters.AddWithValue("value", NpgsqlDbType.Boolean, value);
            await command.ExecuteNonQueryAsync(ct);
        }, cancellationToken);

        var lines = await SelectLinesAsync(orderId, cancellationToken);
        return lines.Count == 0 ? null : lines;
    }

    public async Task<IReadOnlyList<SagaLineRecord>> GetLinesAsync(Guid orderId, CancellationToken cancellationToken) =>
        await SelectLinesAsync(orderId, cancellationToken);

    public Task<IReadOnlyList<(Guid OrderId, SagaOrchestrationRecord Saga)>> ClaimTimedOutAsync(
        TimeSpan timeout,
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            var candidateIds = new List<Guid>();
            await using (var command = new NpgsqlCommand(ClaimCandidatesSql, connection, transaction))
            {
                command.Parameters.AddWithValue("cutoff", NpgsqlDbType.TimestampTz, now - timeout);
                command.Parameters.AddWithValue("batch_size", NpgsqlDbType.Integer, batchSize);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    candidateIds.Add(reader.GetGuid(0));
                }
            }

            if (candidateIds.Count == 0)
            {
                await transaction.CommitAsync(ct);
                return (IReadOnlyList<(Guid, SagaOrchestrationRecord)>)[];
            }

            var idsArray = candidateIds.ToArray();
            var parents = new Dictionary<Guid, SagaOrchestrationRecord>();
            await using (var command = new NpgsqlCommand(SelectParentByIdsSql, connection, transaction))
            {
                command.Parameters.AddWithValue("order_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, idsArray);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var orderId = reader.GetGuid(0);
                    parents[orderId] = new SagaOrchestrationRecord(
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        await reader.GetFieldValueAsync<DateTimeOffset>(5, ct),
                        reader.GetString(6),
                        reader.GetDecimal(7),
                        reader.GetString(8),
                        []);
                }
            }

            var linesByOrder = new Dictionary<Guid, List<SagaLineRecord>>();
            await using (var command = new NpgsqlCommand(SelectLinesForOrdersSql, connection, transaction))
            {
                command.Parameters.AddWithValue("order_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, idsArray);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var orderId = reader.GetGuid(0);
                    if (!linesByOrder.TryGetValue(orderId, out var list))
                    {
                        list = [];
                        linesByOrder[orderId] = list;
                    }

                    list.Add(new SagaLineRecord(
                        reader.GetInt32(1),
                        reader.GetGuid(2),
                        reader.GetString(3),
                        reader.GetInt32(4),
                        await reader.IsDBNullAsync(5, ct) ? null : reader.GetBoolean(5),
                        await reader.IsDBNullAsync(6, ct) ? null : reader.GetBoolean(6),
                        await reader.IsDBNullAsync(7, ct) ? null : reader.GetBoolean(7)));
                }
            }

            await using (var command = new NpgsqlCommand(DeleteByIdsSql, connection, transaction))
            {
                command.Parameters.AddWithValue("order_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, idsArray);
                await command.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);

            var claimed = new List<(Guid, SagaOrchestrationRecord)>();
            foreach (var orderId in candidateIds)
            {
                if (!parents.TryGetValue(orderId, out var parent))
                {
                    continue;
                }

                linesByOrder.TryGetValue(orderId, out var lines);
                claimed.Add((orderId, parent with { Lines = lines ?? [] }));
            }

            return (IReadOnlyList<(Guid, SagaOrchestrationRecord)>)claimed;
        }, cancellationToken).AsTask();
    }

    private async Task<IReadOnlyList<SagaLineRecord>> SelectLinesAsync(Guid orderId, CancellationToken cancellationToken)
    {
        return await _pipeline.ExecuteAsync(async ct =>
        {
            await using var command = dataSource.CreateCommand(SelectLinesSql);
            command.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
            return await ReadLinesAsync(command, ct);
        }, cancellationToken);
    }

    private static async Task<IReadOnlyList<SagaLineRecord>> SelectLinesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid orderId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SelectLinesSql, connection, transaction);
        command.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        return await ReadLinesAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<SagaLineRecord>> ReadLinesAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var lines = new List<SagaLineRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new SagaLineRecord(
                reader.GetInt32(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetInt32(3),
                await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetBoolean(4),
                await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetBoolean(5),
                await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetBoolean(6)));
        }

        return lines;
    }

    private static async Task<SagaOrchestrationRecord> ReadParentAsync(NpgsqlDataReader reader, CancellationToken cancellationToken)
    {
        return new SagaOrchestrationRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            await reader.GetFieldValueAsync<DateTimeOffset>(4, cancellationToken),
            reader.GetString(5),
            reader.GetDecimal(6),
            reader.GetString(7),
            []);
    }
}

public enum SagaLineOutcomeField
{
    Reserved,
    Committed,
    Released
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
