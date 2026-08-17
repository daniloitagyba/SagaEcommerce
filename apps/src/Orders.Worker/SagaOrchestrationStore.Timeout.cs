using BuildingBlocks;
using Npgsql;
using NpgsqlTypes;

namespace Orders.Worker;

public sealed partial class SagaOrchestrationStore
{
    private const string ClaimCandidatesSql = """
        SELECT order_id
        FROM saga_orchestration_states
        WHERE requested_at <= @cutoff
          AND (parked_at IS NULL OR step <> @reserve_step)
        ORDER BY requested_at
        LIMIT @batch_size
        FOR UPDATE SKIP LOCKED;
        """;

    private const string SelectParentByIdsSql = """
        SELECT order_id, correlation_id, customer_id, payment_method, shipping_postal_prefix, requested_at, step, amount, currency, cancellation_requested_at, parked_at
        FROM saga_orchestration_states
        WHERE order_id = ANY(@order_ids);
        """;

    private const string DeleteByIdsSql = """
        DELETE FROM saga_orchestration_states WHERE order_id = ANY(@order_ids);
        """;

    private const string MarkParkedSql = """
        UPDATE saga_orchestration_states
        SET parked_at = COALESCE(parked_at, @parked_at)
        WHERE order_id = @order_id;
        """;

    public Task<IReadOnlyList<(Guid OrderId, SagaOrchestrationRecord Saga)>> ClaimTimedOutAsync(
        TimeSpan timeout,
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken) =>
        ClaimTimedOutCoreAsync(timeout, now, batchSize, (_, _) => [], null, cancellationToken);

    public Task<IReadOnlyList<(Guid OrderId, SagaOrchestrationRecord Saga)>> ClaimTimedOutAndQueueAsync(
        TimeSpan timeout,
        DateTimeOffset now,
        int batchSize,
        Func<Guid, SagaOrchestrationRecord, IReadOnlyList<SagaOutboxCommand>> commandFactory,
        CancellationToken cancellationToken) =>
        ClaimTimedOutCoreAsync(timeout, now, batchSize, commandFactory, null, cancellationToken);

    /// <summary>
    /// Resolves timed-out sagas.
    /// </summary>
    public Task<IReadOnlyList<(Guid OrderId, SagaOrchestrationRecord Saga)>> ClaimTimedOutAndResolveAsync(
        TimeSpan timeout,
        DateTimeOffset now,
        int batchSize,
        Func<Guid, SagaOrchestrationRecord, IReadOnlyList<SagaOutboxCommand>> commandFactory,
        Func<Guid, SagaOrchestrationRecord, NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> applyResolutionAsync,
        CancellationToken cancellationToken) =>
        ClaimTimedOutCoreAsync(timeout, now, batchSize, commandFactory, applyResolutionAsync, cancellationToken);

    private Task<IReadOnlyList<(Guid OrderId, SagaOrchestrationRecord Saga)>> ClaimTimedOutCoreAsync(
        TimeSpan timeout,
        DateTimeOffset now,
        int batchSize,
        Func<Guid, SagaOrchestrationRecord, IReadOnlyList<SagaOutboxCommand>> commandFactory,
        Func<Guid, SagaOrchestrationRecord, NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>? applyResolutionAsync,
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
                command.Parameters.AddWithValue("reserve_step", NpgsqlDbType.Varchar, SagaStep.ReserveInventory);
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
                        [],
                        await reader.IsDBNullAsync(9, ct) ? null : await reader.GetFieldValueAsync<DateTimeOffset>(9, ct),
                        await reader.IsDBNullAsync(10, ct) ? null : await reader.GetFieldValueAsync<DateTimeOffset>(10, ct));
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

            var claimed = new List<(Guid, SagaOrchestrationRecord)>();
            foreach (var orderId in candidateIds)
            {
                if (!parents.TryGetValue(orderId, out var parent))
                {
                    continue;
                }

                linesByOrder.TryGetValue(orderId, out var lines);
                var saga = parent with { Lines = lines ?? [] };
                claimed.Add((orderId, saga));
                await SagaOutboxWriter.EnqueueAsync(
                    connection,
                    transaction,
                    commandFactory(orderId, saga),
                    ct);

                if (applyResolutionAsync is not null)
                {
                    await applyResolutionAsync(orderId, saga, connection, transaction, ct);
                }
            }

            await using (var command = new NpgsqlCommand(DeleteByIdsSql, connection, transaction))
            {
                command.Parameters.AddWithValue("order_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, idsArray);
                await command.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return (IReadOnlyList<(Guid, SagaOrchestrationRecord)>)claimed;
        }, cancellationToken).AsTask();
    }

    /// <summary>
    /// Records a backordered saga line.
    /// </summary>
    public Task MarkParkedAsync(Guid orderId, DateTimeOffset parkedAt, CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            await using var command = dataSource.CreateCommand(MarkParkedSql);
            command.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
            command.Parameters.AddWithValue("parked_at", NpgsqlDbType.TimestampTz, parkedAt);
            await command.ExecuteNonQueryAsync(ct);
        }, cancellationToken).AsTask();
    }
}
