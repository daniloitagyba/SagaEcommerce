namespace Orders.Application.UseCases.ReturnOrder;

/// <summary>
/// Configures the return shipping window.
/// </summary>
public sealed class ReturnOptions
{
    public const string SectionName = "Returns";

    public int RegretWindowDays { get; init; } = 7;
}
