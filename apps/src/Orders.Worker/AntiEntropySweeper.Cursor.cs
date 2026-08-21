using System.Net.Http.Json;
using BuildingBlocks;
using NpgsqlTypes;

namespace Orders.Worker;

public sealed partial class AntiEntropySweeper
{
    private async Task<int> CheckOrdersHaveAccountedPaymentsAsync(CancellationToken cancellationToken)
    {
        var candidates = await GetPaymentCandidateOrdersAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var paymentsClient = httpClientFactory.CreateClient("anti-entropy-payments");
        Dictionary<Guid, string> paymentStatesByOrderId;
        try
        {
            using var response = await paymentsClient.PostAsJsonAsync(
                "/payments/by-orders", candidates.Select(c => c.OrderId).ToList(), cancellationToken);
            response.EnsureSuccessStatusCode();
            var payments = await response.Content.ReadFromJsonAsync<List<PaymentLookupResponse>>(cancellationToken) ?? [];
            paymentStatesByOrderId = payments.ToDictionary(p => p.OrderId, p => p.State);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            AntiEntropyLog.DependencyUnavailable(logger, "payments-service", exception);
            return 0;
        }

        var divergences = 0;
        foreach (var (orderId, orderStatus) in candidates)
        {
            paymentStatesByOrderId.TryGetValue(orderId, out var paymentState);

            if (AntiEntropyChecks.OrderIsMissingAnAccountedPayment(paymentState))
            {
                divergences++;
                OrdersTelemetry.RecordAntiEntropyDivergence("order_missing_accounted_payment");
                AntiEntropyLog.PaymentDivergence(logger, orderId, orderStatus, paymentState ?? "none");
            }
        }

        return divergences;
    }

    private async Task<int> CheckWriteModelMatchesReadModelAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT o.id, o.status, s.status, o.created_at
            FROM orders o
            LEFT JOIN order_summaries s ON s.order_id = o.id
            WHERE ((s.order_id IS NULL AND o.created_at <= @cutoff)
                OR (s.order_id IS NOT NULL AND o.status <> s.status AND s.projected_at <= @cutoff))
              AND (o.created_at, o.id) > (@cursor_created_at, @cursor_id)
            ORDER BY o.created_at, o.id
            LIMIT @batch_size
            """;

        var cutoff = timeProvider.GetUtcNow() - TimeSpan.FromSeconds(_options.ProjectionLagThresholdSeconds);
        var cursor = await GetCursorAsync(WriteReadModelCheckName, cancellationToken);

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("cutoff", NpgsqlDbType.TimestampTz, cutoff);
        command.Parameters.AddWithValue("batch_size", NpgsqlDbType.Integer, _options.BatchSize);
        command.Parameters.AddWithValue("cursor_created_at", NpgsqlDbType.TimestampTz, cursor.CreatedAt);
        command.Parameters.AddWithValue("cursor_id", NpgsqlDbType.Uuid, cursor.Id);

        var divergences = 0;
        var rowCount = 0;
        var lastCreatedAt = cursor.CreatedAt;
        var lastId = cursor.Id;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rowCount++;
                var orderId = reader.GetGuid(0);
                var orderStatus = reader.GetString(1);
                var summaryStatus = await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2);
                lastCreatedAt = await reader.GetFieldValueAsync<DateTimeOffset>(3, cancellationToken);
                lastId = orderId;

                if (AntiEntropyChecks.WriteModelDivergesFromReadModel(orderStatus, summaryStatus))
                {
                    divergences++;
                    OrdersTelemetry.RecordAntiEntropyDivergence("order_write_model_diverges_from_read_model");
                    AntiEntropyLog.WriteReadModelDivergence(logger, orderId, orderStatus, summaryStatus ?? "no summary row");
                }
            }
        }

        await AdvanceCursorAsync(WriteReadModelCheckName, rowCount, _options.BatchSize, lastCreatedAt, lastId, cancellationToken);
        return divergences;
    }

    private async Task<int> CheckOrdersStuckWithoutASagaRowAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT o.id, o.status
            FROM orders o
            WHERE o.status = ANY(@statuses)
              AND o.created_at <= @cutoff
              AND NOT EXISTS (SELECT 1 FROM saga_orchestration_states s WHERE s.order_id = o.id)
            ORDER BY o.created_at
            LIMIT @batch_size
            """;

        var cutoff = timeProvider.GetUtcNow() - TimeSpan.FromSeconds(_options.StuckOrderThresholdSeconds);

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("statuses", NpgsqlDbType.Array | NpgsqlDbType.Varchar, StuckCandidateStatuses);
        command.Parameters.AddWithValue("cutoff", NpgsqlDbType.TimestampTz, cutoff);
        command.Parameters.AddWithValue("batch_size", NpgsqlDbType.Integer, _options.BatchSize);

        var divergences = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            divergences++;
            var orderId = reader.GetGuid(0);
            var orderStatus = reader.GetString(1);
            OrdersTelemetry.RecordAntiEntropyDivergence("order_stuck_without_saga_row");
            AntiEntropyLog.StuckOrderDivergence(logger, orderId, orderStatus);
        }

        return divergences;
    }

    private async Task<IReadOnlyList<(Guid OrderId, string Status)>> GetPaymentCandidateOrdersAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, status, created_at FROM orders
            WHERE status = ANY(@statuses) AND payment_method IS NOT NULL
              AND (created_at, id) > (@cursor_created_at, @cursor_id)
            ORDER BY created_at, id
            LIMIT @batch_size
            """;

        var cursor = await GetCursorAsync(PaymentCheckName, cancellationToken);

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("statuses", NpgsqlDbType.Array | NpgsqlDbType.Varchar, PaymentAccountedStatuses);
        command.Parameters.AddWithValue("batch_size", NpgsqlDbType.Integer, _options.BatchSize);
        command.Parameters.AddWithValue("cursor_created_at", NpgsqlDbType.TimestampTz, cursor.CreatedAt);
        command.Parameters.AddWithValue("cursor_id", NpgsqlDbType.Uuid, cursor.Id);

        var results = new List<(Guid, string)>();
        var lastCreatedAt = cursor.CreatedAt;
        var lastId = cursor.Id;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add((reader.GetGuid(0), reader.GetString(1)));
                lastCreatedAt = await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken);
                lastId = results[^1].Item1;
            }
        }

        await AdvanceCursorAsync(PaymentCheckName, results.Count, _options.BatchSize, lastCreatedAt, lastId, cancellationToken);
        return results;
    }

    private async Task<(DateTimeOffset CreatedAt, Guid Id)> GetCursorAsync(string checkName, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT cursor_created_at, cursor_id FROM anti_entropy_progress WHERE check_name = @check_name");
        command.Parameters.AddWithValue("check_name", NpgsqlDbType.Varchar, checkName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return (await reader.GetFieldValueAsync<DateTimeOffset>(0, cancellationToken), reader.GetGuid(1));
        }

        return (DateTimeOffset.MinValue, Guid.Empty);
    }

    private async Task AdvanceCursorAsync(
        string checkName, int rowsReturned, int batchSize, DateTimeOffset lastCreatedAt, Guid lastId, CancellationToken cancellationToken)
    {
        var (nextCreatedAt, nextId) = rowsReturned < batchSize
            ? (DateTimeOffset.MinValue, Guid.Empty)
            : (lastCreatedAt, lastId);

        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO anti_entropy_progress (check_name, cursor_created_at, cursor_id)
            VALUES (@check_name, @cursor_created_at, @cursor_id)
            ON CONFLICT (check_name) DO UPDATE SET
                cursor_created_at = EXCLUDED.cursor_created_at,
                cursor_id = EXCLUDED.cursor_id
            """);
        command.Parameters.AddWithValue("check_name", NpgsqlDbType.Varchar, checkName);
        command.Parameters.AddWithValue("cursor_created_at", NpgsqlDbType.TimestampTz, nextCreatedAt);
        command.Parameters.AddWithValue("cursor_id", NpgsqlDbType.Uuid, nextId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record PaymentLookupResponse(Guid OrderId, string State, decimal Amount, string Currency);
}
