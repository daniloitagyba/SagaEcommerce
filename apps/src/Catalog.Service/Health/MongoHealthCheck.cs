using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Catalog.Service.Health;

public sealed class MongoHealthCheck(IMongoDatabase database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);

            if (!await HasIndexOnAsync(database, "products", "Sku", cancellationToken)
                || !await HasIndexOnAsync(database, "categories", "Slug", cancellationToken))
            {
                return HealthCheckResult.Unhealthy("MongoDB is reachable but required indexes are missing.");
            }

            return HealthCheckResult.Healthy("MongoDB is reachable and required indexes exist.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("MongoDB is unreachable.", exception);
        }
    }

    private static async Task<bool> HasIndexOnAsync(
        IMongoDatabase database,
        string collectionName,
        string fieldName,
        CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<BsonDocument>(collectionName);
        using var cursor = await collection.Indexes.ListAsync(cancellationToken);
        var indexes = await cursor.ToListAsync(cancellationToken);
        return indexes.Any(index => index["key"].AsBsonDocument.Contains(fieldName));
    }
}
