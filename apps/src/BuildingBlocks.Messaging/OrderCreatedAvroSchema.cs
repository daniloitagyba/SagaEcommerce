using System.Globalization;
using Avro;
using Avro.Generic;

namespace BuildingBlocks;

/// <summary>
/// The Avro wire contract for OrderCreated (Milestone 19), and the one
/// place that converts it to/from the C# record, keeping every consumer in
/// agreement about the mapping. Guid/DateTimeOffset/decimal fields are
/// encoded as strings rather than Avro's logical types, to keep
/// GenericRecord construction simple for a lab-scale schema registry demo.
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

    /// <summary>A v1/v2 writer has no paymentMethod field; absence reads as Pix - an instant charge, not an authorization nobody will capture.</summary>
    private static string ReadPaymentMethod(GenericRecord record)
    {
        return record.TryGetValue("paymentMethod", out var value) && value is string method && method.Length > 0
            ? method
            : PaymentMethods.Pix;
    }

    /// <summary>Absent means "no address given" - an order predating this field should score as unknown, not mismatched.</summary>
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
    /// A v1 message carries no lines field; Avro should fill the default
    /// when read against v2, but this reads defensively in case the
    /// registry hasn't resolved it - both versions are genuinely on the
    /// topic during a rolling deploy.
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
