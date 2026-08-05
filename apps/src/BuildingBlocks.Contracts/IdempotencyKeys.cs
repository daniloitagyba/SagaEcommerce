namespace BuildingBlocks;

public static class IdempotencyKeys
{
    public static string Key(string idempotencyKey) => $"orders:idempotency:{idempotencyKey}";

    public static string LockKey(string idempotencyKey) => $"orders:idempotency-lock:{idempotencyKey}";

    public static string FenceSequenceKey(string idempotencyKey) => $"orders:idempotency-fence-seq:{idempotencyKey}";
}
