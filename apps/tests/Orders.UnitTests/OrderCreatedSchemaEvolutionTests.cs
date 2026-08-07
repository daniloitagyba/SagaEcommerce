using Avro;
using Avro.Generic;
using Avro.IO;
using BuildingBlocks;

namespace Orders.UnitTests;

/// <summary>
/// Milestone 66 adds a lines array to OrderCreated. Schema Registry rejects
/// an incompatible change at publish time, but that says nothing about
/// whether this codebase's reader/writer survives the mixed-version window
/// a rolling deploy creates. These tests encode both halves of that window
/// by round-tripping real Avro binary encoding with deliberately mismatched
/// reader/writer schemas, proving the default actually works.
/// </summary>
public class OrderCreatedSchemaEvolutionTests
{
    private const string V1SchemaJson = """
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

    private static readonly RecordSchema V1Schema = (RecordSchema)Schema.Parse(V1SchemaJson);
    private static readonly RecordSchema V2Schema = OrderCreatedAvroSchema.Schema;

    private static byte[] Encode(GenericRecord record, RecordSchema writerSchema)
    {
        using var stream = new MemoryStream();
        new GenericWriter<GenericRecord>(writerSchema).Write(record, new BinaryEncoder(stream));
        return stream.ToArray();
    }

    private static GenericRecord Decode(byte[] payload, RecordSchema writerSchema, RecordSchema readerSchema)
    {
        using var stream = new MemoryStream(payload);
        return new GenericReader<GenericRecord>(writerSchema, readerSchema)
            .Read(null!, new BinaryDecoder(stream));
    }

    private static GenericRecord BuildV1Record()
    {
        var record = new GenericRecord(V1Schema);
        record.Add("eventId", Guid.NewGuid().ToString());
        record.Add("orderId", Guid.NewGuid().ToString());
        record.Add("customerId", "customer-legacy");
        record.Add("amount", "49.90");
        record.Add("currency", "BRL");
        record.Add("occurredAt", DateTimeOffset.UtcNow.ToString("O"));
        record.Add("correlationId", "correlation-legacy");
        record.Add("schemaVersion", 1);
        return record;
    }

    [Fact]
    public void AV2ConsumerReadsAV1ProducersMessage()
    {
        // Consumers upgrade first, so a new consumer must handle a not-yet-upgraded producer's messages - lines must materialise from the default.
        var payload = Encode(BuildV1Record(), V1Schema);

        var decoded = Decode(payload, V1Schema, V2Schema);
        var orderCreated = OrderCreatedAvroSchema.FromGenericRecord(decoded);

        Assert.Equal(OrderCreatedSchemaVersions.AmountOnly, orderCreated.SchemaVersion);
        Assert.Empty(orderCreated.LinesOrEmpty);
        Assert.False(orderCreated.HasLineItems);
        Assert.Equal(49.90m, orderCreated.Amount);
        Assert.True(OrderCreatedSchemaVersions.IsSupported(orderCreated.SchemaVersion));
    }

    [Fact]
    public void AV1ConsumerReadsAV2ProducersMessageIgnoringTheLines()
    {
        // The other half: a producer writing lines while a consumer still runs the old schema. Avro drops the unknown field.
        var orderCreated = new OrderCreated(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "customer-1",
            120m,
            "BRL",
            DateTimeOffset.UtcNow,
            "correlation-1",
            [new OrderCreatedLine("SKU-ELEC-001", "Headphones", 2, 60m, 120m)]);

        var payload = Encode(OrderCreatedAvroSchema.ToGenericRecord(orderCreated), V2Schema);

        var decoded = Decode(payload, V2Schema, V1Schema);

        Assert.Equal("customer-1", (string)decoded["customerId"]);
        Assert.Equal("120", (string)decoded["amount"]);
        Assert.False(decoded.TryGetValue("lines", out _));
    }

    [Fact]
    public void RoundTripsLineItemsThroughV2()
    {
        var orderCreated = new OrderCreated(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "customer-1",
            230.50m,
            "BRL",
            DateTimeOffset.UtcNow,
            "correlation-1",
            [
                new OrderCreatedLine("SKU-ELEC-001", "Headphones", 2, 60m, 120m),
                new OrderCreatedLine("SKU-BOOK-001", "Clean Architecture", 1, 110.50m, 110.50m)
            ]);

        var payload = Encode(OrderCreatedAvroSchema.ToGenericRecord(orderCreated), V2Schema);
        var decoded = OrderCreatedAvroSchema.FromGenericRecord(Decode(payload, V2Schema, V2Schema));

        // The constant, not a literal - asserts "whatever the current writer emits", which caught the shift when Milestone 68 moved the default to v3.
        Assert.Equal(OrderCreatedSchemaVersions.WithShippingPrefix, decoded.SchemaVersion);
        Assert.True(decoded.HasLineItems);
        Assert.Collection(
            decoded.LinesOrEmpty,
            line =>
            {
                Assert.Equal("SKU-ELEC-001", line.Sku);
                Assert.Equal(2, line.Quantity);
                Assert.Equal(60m, line.UnitPrice);
                Assert.Equal(120m, line.LineTotal);
            },
            line =>
            {
                Assert.Equal("SKU-BOOK-001", line.Sku);
                Assert.Equal(110.50m, line.UnitPrice);
            });
    }

    [Fact]
    public void EverySchemaVersionThisConsumerCanReadIsAccepted()
    {
        Assert.True(OrderCreatedSchemaVersions.IsSupported(OrderCreatedSchemaVersions.AmountOnly));
        Assert.True(OrderCreatedSchemaVersions.IsSupported(OrderCreatedSchemaVersions.WithLineItems));
        Assert.True(OrderCreatedSchemaVersions.IsSupported(OrderCreatedSchemaVersions.WithPaymentMethod));
        Assert.True(OrderCreatedSchemaVersions.IsSupported(OrderCreatedSchemaVersions.WithShippingPrefix));
        Assert.False(OrderCreatedSchemaVersions.IsSupported(99));
    }

    [Fact]
    public void AV3ConsumerReadsAV1ProducersMessageAsAnInstantPix()
    {
        // Reading a missing method as Card would leave an authorization on a payment charged outright, with no capture command ever arriving.
        var payload = Encode(BuildV1Record(), V1Schema);

        var orderCreated = OrderCreatedAvroSchema.FromGenericRecord(Decode(payload, V1Schema, V2Schema));

        Assert.Equal(PaymentMethods.Pix, orderCreated.PaymentMethod);
        Assert.False(PaymentMethods.RequiresCapture(orderCreated.PaymentMethod));
    }

    [Fact]
    public void RoundTripsTheChosenPaymentMethodThroughV3()
    {
        var orderCreated = new OrderCreated(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "customer-1",
            120m,
            "BRL",
            DateTimeOffset.UtcNow,
            "correlation-1",
            [new OrderCreatedLine("SKU-ELEC-001", "Headphones", 2, 60m, 120m)],
            PaymentMethod: PaymentMethods.Card);

        var payload = Encode(OrderCreatedAvroSchema.ToGenericRecord(orderCreated), V2Schema);
        var decoded = OrderCreatedAvroSchema.FromGenericRecord(Decode(payload, V2Schema, V2Schema));

        Assert.Equal(PaymentMethods.Card, decoded.PaymentMethod);
        Assert.True(PaymentMethods.RequiresCapture(decoded.PaymentMethod));
        Assert.Equal(OrderCreatedSchemaVersions.WithShippingPrefix, decoded.SchemaVersion);
    }

    [Fact]
    public void AV4ConsumerReadsAV1ProducersMessageAsHavingNoKnownAddress()
    {
        // Milestone 73: the default has to be "unknown", not "somewhere" - an empty prefix says a pre-field order has nothing to compare.
        var payload = Encode(BuildV1Record(), V1Schema);

        var orderCreated = OrderCreatedAvroSchema.FromGenericRecord(Decode(payload, V1Schema, V2Schema));

        Assert.Equal(string.Empty, orderCreated.ShippingPostalPrefix);
    }

    [Fact]
    public void RoundTripsTheShippingPrefixThroughV4()
    {
        var orderCreated = new OrderCreated(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "customer-1",
            120m,
            "BRL",
            DateTimeOffset.UtcNow,
            "correlation-1",
            [new OrderCreatedLine("SKU-ELEC-001", "Headphones", 2, 60m, 120m)],
            PaymentMethod: PaymentMethods.Boleto,
            ShippingPostalPrefix: "66");

        var payload = Encode(OrderCreatedAvroSchema.ToGenericRecord(orderCreated), V2Schema);
        var decoded = OrderCreatedAvroSchema.FromGenericRecord(Decode(payload, V2Schema, V2Schema));

        Assert.Equal("66", decoded.ShippingPostalPrefix);
        Assert.Equal(PaymentMethods.Boleto, decoded.PaymentMethod);
    }
}
