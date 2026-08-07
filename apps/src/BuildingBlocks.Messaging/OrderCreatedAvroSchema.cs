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
            { "name": "schemaVersion", "type": "int", "default": 1 },
            {
              "name": "lines",
              "type": {
                "type": "array",
                "items": {
                  "type": "record",
                  "name": "OrderLine",
                  "fields": [
                    { "name": "sku", "type": "string" },
                    { "name": "productName", "type": "string" },
                    { "name": "quantity", "type": "int" },
                    { "name": "unitPrice", "type": "string" },
                    { "name": "lineTotal", "type": "string" }
                  ]
                }
              },
              "default": []
            },
            { "name": "paymentMethod", "type": "string", "default": "Pix" },
            { "name": "shippingPostalPrefix", "type": "string", "default": "" }
          ]
        }
        """;

    public static readonly RecordSchema Schema = (RecordSchema)Avro.Schema.Parse(SchemaJson);

    private static readonly RecordSchema LineSchema =
        (RecordSchema)((ArraySchema)Schema["lines"].Schema).ItemSchema;

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
        record.Add("lines", orderCreated.LinesOrEmpty.Select(ToLineRecord).ToArray());
        record.Add("paymentMethod", orderCreated.PaymentMethod);
        record.Add("shippingPostalPrefix", orderCreated.ShippingPostalPrefix);
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
            ReadLines(record),
            (int)record["schemaVersion"],
            ReadPaymentMethod(record),
            ReadShippingPostalPrefix(record));
    }

    /// <summary>
    /// Defensive for the same reason ReadLines is: a message written by a
    /// v1/v2 writer has no paymentMethod field at all, and treating its
    /// absence as Pix is the safe reading - an instant charge rather than
    /// an authorization no capture command will ever arrive for.
    /// </summary>
    private static string ReadPaymentMethod(GenericRecord record)
    {
        return record.TryGetValue("paymentMethod", out var value) && value is string method && method.Length > 0
            ? method
            : PaymentMethods.Pix;
    }

    /// <summary>
    /// Defensive for the same reason ReadPaymentMethod is. Absent means
    /// "no address was given", which is exactly how an order created before
    /// this field existed should be scored - unknown, not mismatched.
    /// </summary>
    private static string ReadShippingPostalPrefix(GenericRecord record)
    {
        return record.TryGetValue("shippingPostalPrefix", out var value) && value is string prefix
            ? prefix
            : string.Empty;
    }

    private static GenericRecord ToLineRecord(OrderCreatedLine line)
    {
        var record = new GenericRecord(LineSchema);
        record.Add("sku", line.Sku);
        record.Add("productName", line.ProductName);
        record.Add("quantity", line.Quantity);
        record.Add("unitPrice", line.UnitPrice.ToString(CultureInfo.InvariantCulture));
        record.Add("lineTotal", line.LineTotal.ToString(CultureInfo.InvariantCulture));
        return record;
    }

    /// <summary>
    /// A v1 producer's message carries no lines field at all. Avro fills it
    /// from the schema default when read against v2, but a message written
    /// by a v1 <em>writer schema</em> and deserialized before the registry
    /// resolves the default would leave it absent - so this reads
    /// defensively rather than indexing straight in. Same reason
    /// OrderCreatedSchemaVersions.IsSupported accepts both versions:
    /// during a rolling deploy, both are genuinely on the topic.
    /// </summary>
    private static IReadOnlyList<OrderCreatedLine> ReadLines(GenericRecord record)
    {
        if (!record.TryGetValue("lines", out var rawLines) || rawLines is not object[] lines)
        {
            return [];
        }

        return [.. lines.OfType<GenericRecord>().Select(line => new OrderCreatedLine(
            (string)line["sku"],
            (string)line["productName"],
            (int)line["quantity"],
            decimal.Parse((string)line["unitPrice"], CultureInfo.InvariantCulture),
            decimal.Parse((string)line["lineTotal"], CultureInfo.InvariantCulture)))];
    }
}
