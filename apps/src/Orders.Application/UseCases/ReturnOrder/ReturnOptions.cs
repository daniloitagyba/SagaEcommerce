namespace Orders.Application.UseCases.ReturnOrder;

public sealed class ReturnOptions
{
    public const string SectionName = "Returns";

    public int RegretWindowDays { get; init; } = 7;
}
