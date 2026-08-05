namespace BuildingBlocks;

public static class OrderCacheKeys
{
    public static string Key(Guid orderId) => $"orders:cache:{orderId}";

    public static string LockKey(Guid orderId) => $"orders:cache-lock:{orderId}";

    public static string FenceSequenceKey(Guid orderId) => $"orders:cache-fence-seq:{orderId}";
}
