using System.Globalization;
using Avro;
using Avro.Generic;

namespace BuildingBlocks;

/// <summary>
/// The Avro wire contract for OrderCreated (Milestone 19), and the one place
/// that converts between it and the C# record - keeping the producer and
/// every consumer in agreement about the mapping instead of each hand-rolling
/// their own. Guid/DateTimeOffset fields are encoded as strings rather than
/// Avro's uuid/timestamp logical types, and amount as a decimal-formatted
/// string rather than Avro's decimal logical type, to keep GenericRecord
/// construction straightforward without adding byte-level encoding code for
/// a lab-scale demonstration of schema registry mechanics.
/// </summary>
public static class OrderCreatedAvroSchema
{
    public const string SchemaJson = """
        {
          "type": "record",
          "name": "OrderCreated",
          "namespace": "local_distributed_lab.orders",
          "fields": [
            { "name": "eventId", "type": "string" },
            { "name": "orderId", "type": "string" },
            { "name": "customerId", "type": "string" },
            { "name": "amount", "type": "string" },
            { "name": "currency", "type": "string" },
            { "name": "occurredAt", "type": "string" },
            { "name": "correlationId", "type": "string" },
            { "name": "schemaVersion", "type": "int", "default": 1 }
          ]
        }
        """;

    public static readonly RecordSchema Schema = (RecordSchema)Avro.Schema.Parse(SchemaJson);

    public static GenericRecord ToGenericRecord(OrderCreated orderCreated)
    {
        var record = new GenericRecord(Schema);
        record.Add("eventId", orderCreated.EventId.ToString());
        record.Add("orderId", orderCreated.OrderId.ToString());
        record.Add("customerId", orderCreated.CustomerId);
        record.Add("amount", orderCreated.Amount.ToString(CultureInfo.InvariantCulture));
        record.Add("currency", orderCreated.Currency);
        record.Add("occurredAt", orderCreated.OccurredAt.ToString("O", CultureInfo.InvariantCulture));
        record.Add("correlationId", orderCreated.CorrelationId);
        record.Add("schemaVersion", orderCreated.SchemaVersion);
        return record;
    }

    public static OrderCreated FromGenericRecord(GenericRecord record)
    {
        return new OrderCreated(
            Guid.Parse((string)record["eventId"]),
            Guid.Parse((string)record["orderId"]),
            (string)record["customerId"],
            decimal.Parse((string)record["amount"], CultureInfo.InvariantCulture),
            (string)record["currency"],
            DateTimeOffset.Parse((string)record["occurredAt"], CultureInfo.InvariantCulture),
            (string)record["correlationId"],
            (int)record["schemaVersion"]);
    }
}
