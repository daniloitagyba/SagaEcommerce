using FluentValidation;
using FluentValidation.Results;

namespace Orders.Application.UseCases.CreateOrder;

/// <summary>
/// Milestone 66 replaced a hand-rolled Dictionary-building validator with
/// FluentValidation. The trigger was the request growing a conditional
/// shape: it now arrives either as line items (where Amount and Currency
/// are computed server-side from the catalog) or in the legacy amount-only
/// form (where the client must supply them), plus per-item rules needing
/// indexed error keys like Items[2].Quantity. Expressing "these rules
/// apply only in this shape" is precisely what the hand-rolled version had
/// started to reimplement.
///
/// The static Validate/Normalize surface is kept deliberately so no caller
/// outside this file had to change.
/// </summary>
public sealed class CreateOrderCommandRules : AbstractValidator<CreateOrderCommand>
{
    public const int MaximumItems = 50;
    public const int MaximumQuantityPerItem = 100;

    public CreateOrderCommandRules()
    {
        RuleFor(command => command.CustomerId)
            .NotEmpty().WithMessage("CustomerId is required and must not exceed 100 characters.")
            .MaximumLength(100).WithMessage("CustomerId is required and must not exceed 100 characters.");

        When(command => command.IsLineItemCheckout, () =>
        {
            RuleFor(command => command.Items!)
                .Must(items => items.Count <= MaximumItems)
                .WithMessage($"An order must not exceed {MaximumItems} distinct items.")
                .OverridePropertyName(nameof(CreateOrderCommand.Items));

            RuleFor(command => command.Items!)
                .Must(HaveNoDuplicateSkus)
                .WithMessage("Each SKU must appear at most once; combine duplicates into a single quantity.")
                .OverridePropertyName(nameof(CreateOrderCommand.Items));

            RuleForEach(command => command.Items!).ChildRules(item =>
            {
                item.RuleFor(entry => entry.Sku)
                    .NotEmpty().WithMessage("Sku is required.")
                    .MaximumLength(64).WithMessage("Sku must not exceed 64 characters.");

                item.RuleFor(entry => entry.Quantity)
                    .GreaterThan(0).WithMessage("Quantity must be greater than zero.")
                    .LessThanOrEqualTo(MaximumQuantityPerItem)
                    .WithMessage($"Quantity must not exceed {MaximumQuantityPerItem} per item.");
            });

            RuleFor(command => command.CouponCode!)
                .MaximumLength(64).WithMessage("CouponCode must not exceed 64 characters.")
                .When(command => !string.IsNullOrWhiteSpace(command.CouponCode));

            // Milestone 68: an unrecognised method is rejected rather than
            // quietly falling back to Pix - silently charging by a
            // different method than the shopper picked is worse than
            // refusing the order.
            RuleFor(command => command.PaymentMethod!)
                .Must(BuildingBlocks.PaymentMethods.IsSupported)
                .WithMessage($"PaymentMethod must be one of: {BuildingBlocks.PaymentMethods.Card}, {BuildingBlocks.PaymentMethods.Pix}, {BuildingBlocks.PaymentMethods.Boleto}.")
                .When(command => !string.IsNullOrWhiteSpace(command.PaymentMethod));
        });

        // The Milestone 7 shape: the client states the amount because there
        // are no line items to price it from.
        When(command => !command.IsLineItemCheckout, () =>
        {
            RuleFor(command => command.Amount)
                .GreaterThan(0).WithMessage("Amount must be greater than zero.");

            RuleFor(command => command.Currency)
                .NotEmpty().WithMessage("Currency is required.");

            RuleFor(command => command.Currency)
                .Must(BeAThreeLetterCode).WithMessage("Currency must be a three-letter code.")
                .When(command => !string.IsNullOrWhiteSpace(command.Currency));
        });
    }

    private static bool HaveNoDuplicateSkus(IReadOnlyList<CreateOrderItem> items) =>
        items
            .Where(item => !string.IsNullOrWhiteSpace(item.Sku))
            .Select(item => item.Sku!.Trim())
            .GroupBy(sku => sku, StringComparer.OrdinalIgnoreCase)
            .All(group => group.Count() == 1);

    private static bool BeAThreeLetterCode(string? currency) =>
        currency is { Length: 3 } && currency.All(char.IsLetter);
}

public static class CreateOrderCommandValidator
{
    private static readonly CreateOrderCommandRules Rules = new();

    public static Dictionary<string, string[]> Validate(CreateOrderCommand command)
    {
        var result = Rules.Validate(command);
        return result.IsValid ? [] : ToProblemDictionary(result);
    }

    private static Dictionary<string, string[]> ToProblemDictionary(ValidationResult result) =>
        result.Errors
            .GroupBy(failure => failure.PropertyName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(failure => failure.ErrorMessage).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

    public static string NormalizeCustomerId(string customerId) => customerId.Trim();

    public static string NormalizeCurrency(string currency) => currency.Trim().ToUpperInvariant();
}
