using BuildingBlocks;
using Npgsql;
using NpgsqlTypes;
using Polly;
using Polly.Registry;

namespace Orders.Worker;

public sealed partial class SagaOrchestrationStore(NpgsqlDataSource dataSource, ResiliencePipelineProvider<string> pipelineProvider)
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
        RETURNING correlation_id, customer_id, payment_method, shipping_postal_prefix, requested_at, step, amount, currency, cancellation_requested_at, parked_at;
        """;

    private const string TryCompleteSql = """
        DELETE FROM saga_orchestration_states
        WHERE order_id = @order_id AND step = @expected_step
        RETURNING correlation_id, customer_id, payment_method, shipping_postal_prefix, requested_at, step, amount, currency, cancellation_requested_at, parked_at;
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

    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresTransactionPipeline);
    private readonly ResiliencePipeline _readPipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresPipeline);

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
        CancellationToken cancellationToken) =>
        TrackReserveRequestedAndQueueAsync(
            orderId,
            correlationId,
            customerId,
            paymentMethod,
            shippingPostalPrefix,
            lines,
            amount,
            currency,
            requestedAt,
            [],
            cancellationToken);

    public Task TrackReserveRequestedAndQueueAsync(
        Guid orderId,
        string correlationId,
        string customerId,
        string paymentMethod,
        string shippingPostalPrefix,
        IReadOnlyList<SagaReservationLine> lines,
        decimal amount,
        string currency,
        DateTimeOffset requestedAt,
        IReadOnlyList<SagaOutboxCommand> outboxCommands,
        CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            int insertedParent;
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
                insertedParent = await command.ExecuteNonQueryAsync(ct);
            }

            if (insertedParent > 0)
            {
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

                await SagaOutboxWriter.EnqueueAsync(connection, transaction, outboxCommands, ct);
            }

            await transaction.CommitAsync(ct);
        }, cancellationToken).AsTask();
    }

    public Task<SagaOrchestrationRecord?> TryAdvanceAsync(
        Guid orderId,
        string expectedCurrentStep,
        string nextStep,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken) =>
        TryAdvanceAndQueueAsync(
            orderId,
            expectedCurrentStep,
            nextStep,
            requestedAt,
            _ => [],
            cancellationToken);

    public Task<SagaOrchestrationRecord?> TryAdvanceAndQueueAsync(
        Guid orderId,
        string expectedCurrentStep,
        string nextStep,
        DateTimeOffset requestedAt,
        Func<SagaOrchestrationRecord, IReadOnlyList<SagaOutboxCommand>> commandFactory,
        CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            SagaOrchestrationRecord? parent;

            await using (var command = new NpgsqlCommand(TryAdvanceSql, connection, transaction))
            {
                command.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
                command.Parameters.AddWithValue("expected_step", NpgsqlDbType.Varchar, expectedCurrentStep);
                command.Parameters.AddWithValue("next_step", NpgsqlDbType.Varchar, nextStep);
                command.Parameters.AddWithValue("requested_at", NpgsqlDbType.TimestampTz, requestedAt);
                await using var reader = await command.ExecuteReaderAsync(ct);
                parent = await reader.ReadAsync(ct) ? await ReadParentAsync(reader, ct) : null;
            }

            if (parent is null)
            {
                await transaction.RollbackAsync(ct);
                return null;
            }

            var lines = await SelectLinesAsync(connection, transaction, orderId, ct);
            var result = parent with { Lines = lines };
            await SagaOutboxWriter.EnqueueAsync(connection, transaction, commandFactory(result), ct);
            await transaction.CommitAsync(ct);
            return result;
        }, cancellationToken).AsTask();
    }

    public Task<SagaOrchestrationRecord?> TryCompleteAsync(
        Guid orderId,
        string expectedCurrentStep,
        CancellationToken cancellationToken) =>
        TryCompleteCoreAsync(orderId, expectedCurrentStep, _ => [], null, cancellationToken);

    public Task<SagaOrchestrationRecord?> TryCompleteAndQueueAsync(
        Guid orderId,
        string expectedCurrentStep,
        Func<SagaOrchestrationRecord, IReadOnlyList<SagaOutboxCommand>> commandFactory,
        CancellationToken cancellationToken) =>
        TryCompleteCoreAsync(orderId, expectedCurrentStep, commandFactory, null, cancellationToken);

    public Task<SagaOrchestrationRecord?> TryCompleteAndResolveAsync(
        Guid orderId,
        string expectedCurrentStep,
        Func<SagaOrchestrationRecord, IReadOnlyList<SagaOutboxCommand>> commandFactory,
        Func<SagaOrchestrationRecord, NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> applyStatusTransitionAsync,
        CancellationToken cancellationToken) =>
        TryCompleteCoreAsync(orderId, expectedCurrentStep, commandFactory, applyStatusTransitionAsync, cancellationToken);

    private Task<SagaOrchestrationRecord?> TryCompleteCoreAsync(
        Guid orderId,
        string expectedCurrentStep,
        Func<SagaOrchestrationRecord, IReadOnlyList<SagaOutboxCommand>> commandFactory,
        Func<SagaOrchestrationRecord, NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>? applyStatusTransitionAsync,
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

            if (parent is null)
            {
                await transaction.RollbackAsync(ct);
                return null;
            }

            var result = parent with { Lines = lines };
            await SagaOutboxWriter.EnqueueAsync(connection, transaction, commandFactory(result), ct);

            if (applyStatusTransitionAsync is not null)
            {
                await applyStatusTransitionAsync(result, connection, transaction, ct);
            }

            await transaction.CommitAsync(ct);
            return result;
        }, cancellationToken).AsTask();
    }

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

    private async Task<IReadOnlyList<SagaLineRecord>> SelectLinesAsync(Guid orderId, CancellationToken cancellationToken)
    {
        return await _readPipeline.ExecuteAsync(async ct =>
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
            [],
            await reader.IsDBNullAsync(8, cancellationToken) ? null : await reader.GetFieldValueAsync<DateTimeOffset>(8, cancellationToken),
            await reader.IsDBNullAsync(9, cancellationToken) ? null : await reader.GetFieldValueAsync<DateTimeOffset>(9, cancellationToken));
    }
}
