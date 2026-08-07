using System.Globalization;
using System.Text;
using System.Text.Json;
using BuildingBlocks;
using Confluent.Kafka;

// Milestone 62: the five dead-letter topics have been write-only since they
// were introduced - publishers exist, nothing consumes them. Generic and
// standalone, not tied to any one service, since every publisher writes
// the same DeadLetterEnvelope JSON shape and header set.
if (args.Length == 0 || (args[0] != "inspect" && args[0] != "redrive"))
{
    Console.Error.WriteLine("Usage: DlqRedriveTool <inspect|redrive> --bootstrap-servers <host:port> --topic <dlq-topic> [--max-redrives N] [--dry-run] [--idle-seconds N] [--key-filter <substring>]");
    return 1;
}

var mode = args[0];
var options = ParseOptions(args);

if (options.BootstrapServers is null || options.Topic is null)
{
    Console.Error.WriteLine("--bootstrap-servers and --topic are required.");
    return 1;
}

return mode == "inspect"
    ? await InspectAsync(options)
    : await RedriveAsync(options);

static ToolOptions ParseOptions(string[] arguments)
{
    string? bootstrapServers = null;
    string? topic = null;
    var maxRedrives = 3;
    var dryRun = false;
    var idleSeconds = 3;
    string? keyFilter = null;

    for (var i = 1; i < arguments.Length; i++)
    {
        switch (arguments[i])
        {
            case "--bootstrap-servers":
                bootstrapServers = arguments[++i];
                break;
            case "--topic":
                topic = arguments[++i];
                break;
            case "--max-redrives":
                maxRedrives = int.Parse(arguments[++i], CultureInfo.InvariantCulture);
                break;
            case "--idle-seconds":
                idleSeconds = int.Parse(arguments[++i], CultureInfo.InvariantCulture);
                break;
            case "--dry-run":
                dryRun = true;
                break;
            case "--key-filter":
                keyFilter = arguments[++i];
                break;
        }
    }

    return new ToolOptions(bootstrapServers, topic, maxRedrives, dryRun, idleSeconds, keyFilter);
}

static async Task<int> InspectAsync(ToolOptions options)
{
    using var consumer = BuildConsumer(options.BootstrapServers!, $"dlq-inspect-{Guid.NewGuid():N}");
    consumer.Subscribe(options.Topic);

    var byFailureType = new Dictionary<string, int> {};
    var total = 0;

    await foreach (var consumeResult in DrainAsync(consumer, options.IdleSeconds))
    {
        var envelope = Decode(consumeResult);
        total++;
        byFailureType[envelope.FailureType] = byFailureType.GetValueOrDefault(envelope.FailureType) + 1;

        var redriveCount = GetRedriveCount(consumeResult.Message.Headers);
        Console.WriteLine(
            $"[{envelope.FailedAt:O}] originalTopic={envelope.OriginalTopic} key={envelope.OriginalKey} " +
            $"failureType={envelope.FailureType} attempts={envelope.AttemptCount} redriveCount={redriveCount} " +
            $"message=\"{envelope.FailureMessage}\"");
    }

    Console.WriteLine();
    Console.WriteLine($"== {options.Topic}: {total} message(s) ==");
    foreach (var (failureType, count) in byFailureType.OrderByDescending(kvp => kvp.Value))
    {
        Console.WriteLine($"  {failureType}: {count}");
    }

    consumer.Close();
    return 0;
}

static async Task<int> RedriveAsync(ToolOptions options)
{
    using var consumer = BuildConsumer(options.BootstrapServers!, $"dlq-redrive-{Guid.NewGuid():N}");
    consumer.Subscribe(options.Topic);
    using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = options.BootstrapServers }).Build();

    var redriven = 0;
    var skippedOverCap = 0;
    var read = 0;
    var errored = 0;
    var filteredOut = 0;

    await foreach (var consumeResult in DrainAsync(consumer, options.IdleSeconds))
    {
        read++;
        try
        {
            var envelope = Decode(consumeResult);

            if (options.KeyFilter is not null
                && (envelope.OriginalKey is null || !envelope.OriginalKey.Contains(options.KeyFilter, StringComparison.Ordinal)))
            {
                // Not committed - a filtered run is a scoped pass, not a verdict on the rest of the topic.
                filteredOut++;
                continue;
            }

            var redriveCount = GetRedriveCount(consumeResult.Message.Headers);

            if (redriveCount >= options.MaxRedrives)
            {
                skippedOverCap++;
                Console.WriteLine(
                    $"SKIP (redriveCount={redriveCount} >= max={options.MaxRedrives}): originalTopic={envelope.OriginalTopic} key={envelope.OriginalKey}");
                if (!options.DryRun)
                {
                    consumer.Commit(consumeResult);
                }

                continue;
            }

            if (options.DryRun)
            {
                Console.WriteLine(
                    $"WOULD REDRIVE (attempt {redriveCount + 1}): originalTopic={envelope.OriginalTopic} key={envelope.OriginalKey} " +
                    $"failureType={envelope.FailureType}");
                continue;
            }

            if (envelope.OriginalKey is null)
            {
                // Every producer keys its messages; a null key here would silently break the ordering guarantee on redrive. Flag it instead of guessing.
                Console.WriteLine($"SKIP (null original key, cannot safely re-key): originalTopic={envelope.OriginalTopic}");
                skippedOverCap++;
                continue;
            }

            var headers = new Headers();
            CopyHeader(consumeResult.Message.Headers, headers, MessagingHeaders.CorrelationId);
            CopyHeader(consumeResult.Message.Headers, headers, MessagingHeaders.TraceParent);
            CopyHeader(consumeResult.Message.Headers, headers, MessagingHeaders.TraceState);
            headers.Add(MessagingHeaders.RedriveCount, Encoding.UTF8.GetBytes((redriveCount + 1).ToString(CultureInfo.InvariantCulture)));
            headers.Add("redriven-from", Encoding.UTF8.GetBytes(options.Topic!));

            var message = new Message<string, byte[]>
            {
                Key = envelope.OriginalKey,
                Value = DecodeOriginalPayload(envelope.OriginalPayload),
                Headers = headers
            };

            await producer.ProduceAsync(envelope.OriginalTopic, message);
            consumer.Commit(consumeResult);
            redriven++;
            Console.WriteLine($"REDRIVEN (attempt {redriveCount + 1}): originalTopic={envelope.OriginalTopic} key={envelope.OriginalKey}");
        }
        catch (Exception exception)
        {
            // One malformed envelope shouldn't sink the batch - not committed, so it's retried next run, not lost.
            errored++;
            Console.WriteLine($"ERROR decoding/redriving offset {consumeResult.TopicPartitionOffset}: {exception.Message}");
        }
    }

    Console.WriteLine();
    Console.WriteLine(
        $"== {options.Topic}: read={read} redriven={redriven} skippedOverCap={skippedOverCap} errored={errored} filteredOut={filteredOut} dryRun={options.DryRun} ==");

    consumer.Close();
    return 0;
}

// Not every *DeadLetterPublisher agrees on the wire format: Orders.Worker
// and Payments.Service base64-encode the payload (their consumers read
// byte[], some topics carry Avro); Inventory.Service's are plain string
// consumers and store raw JSON instead. Try base64 first, fall back to raw
// UTF-8 rather than assuming one convention.
static byte[] DecodeOriginalPayload(string originalPayload)
{
    try
    {
        return Convert.FromBase64String(originalPayload);
    }
    catch (FormatException)
    {
        return Encoding.UTF8.GetBytes(originalPayload);
    }
}

static IConsumer<string, string> BuildConsumer(string bootstrapServers, string groupId)
{
    var config = new ConsumerConfig
    {
        BootstrapServers = bootstrapServers,
        GroupId = groupId,
        AutoOffsetReset = AutoOffsetReset.Earliest,
        EnableAutoCommit = false,
        EnableAutoOffsetStore = false,
        AllowAutoCreateTopics = false
    };

    return new ConsumerBuilder<string, string>(config).Build();
}

// Drains what's currently in the topic and stops - a point-in-time
// operator action, not a long-running consumer. Idle-seconds of empty
// polls is the "caught up" signal, since there's no partition assignment
// to query watermark offsets against before the first poll.
static async IAsyncEnumerable<ConsumeResult<string, string>> DrainAsync(IConsumer<string, string> consumer, int idleSeconds)
{
    var idlePolls = 0;
    while (idlePolls < idleSeconds)
    {
        var consumeResult = await Task.Run(() => consumer.Consume(TimeSpan.FromSeconds(1)));
        if (consumeResult is null || consumeResult.IsPartitionEOF)
        {
            idlePolls++;
            continue;
        }

        idlePolls = 0;
        yield return consumeResult;
    }
}

static DeadLetterEnvelopeView Decode(ConsumeResult<string, string> consumeResult)
{
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    return JsonSerializer.Deserialize<DeadLetterEnvelopeView>(consumeResult.Message.Value, options)
        ?? throw new InvalidOperationException($"Could not decode dead-letter envelope at offset {consumeResult.Offset.Value}.");
}

static int GetRedriveCount(Headers headers)
{
    var header = headers.LastOrDefault(item => string.Equals(item.Key, MessagingHeaders.RedriveCount, StringComparison.Ordinal));
    if (header is null)
    {
        return 0;
    }

    return int.TryParse(Encoding.UTF8.GetString(header.GetValueBytes()), out var count) ? count : 0;
}

static void CopyHeader(Headers source, Headers destination, string key)
{
    var header = source.LastOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
    if (header is not null)
    {
        destination.Add(key, header.GetValueBytes());
    }
}

internal sealed record ToolOptions(string? BootstrapServers, string? Topic, int MaxRedrives, bool DryRun, int IdleSeconds, string? KeyFilter);

// Mirrors the wire shape every *DeadLetterPublisher produces - a
// standalone copy, not a project reference, since this tool has no
// business depending on any one service's assembly.
internal sealed record DeadLetterEnvelopeView(
    Guid DeadLetterId,
    string OriginalTopic,
    int OriginalPartition,
    long OriginalOffset,
    string? OriginalKey,
    string OriginalPayload,
    string FailureType,
    string FailureMessage,
    int AttemptCount,
    DateTimeOffset FailedAt);
