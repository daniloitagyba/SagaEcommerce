namespace BuildingBlocks;

public sealed class SchemaRegistryOptions
{
    public const string SectionName = "SchemaRegistry";

    public string Url { get; init; } = string.Empty;
}
