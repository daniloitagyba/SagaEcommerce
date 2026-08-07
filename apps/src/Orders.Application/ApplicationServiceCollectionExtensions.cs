using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.FeatureManagement;
using Orders.Application.Pricing;
using Orders.Application.UseCases.CreateOrder;
using Orders.Domain.Pricing;
using Orders.Application.UseCases.GetOrder;
using Orders.Application.UseCases.GetOrderHistory;
using Orders.Application.UseCases.ListOrderSummaries;

namespace Orders.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddOrdersApplication(this IServiceCollection services)
    {
        services.AddFeatureManagement();
        // Compiling the NRules Rete network is expensive and the result is
        // immutable and thread-safe, so the engine is a singleton and each
        // Price call takes a cheap session off it.
        services.TryAddSingleton<IPricingEngine, NRulesPricingEngine>();
        // Milestone 67: coupon validity windows are evaluated against this
        // clock, so it is injected rather than read from DateTimeOffset.UtcNow -
        // otherwise "expired last night" is untestable without waiting.
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<OrderPricingService>();
        services.AddScoped<CreateOrderHandler>();
        services.AddScoped<UseCases.AdvanceFulfillment.AdvanceFulfillmentHandler>();
        services.AddScoped<UseCases.ReturnOrder.ReturnOrderHandler>();
        services.AddScoped<GetOrderHandler>();
        services.AddScoped<ListOrderSummariesHandler>();
        services.AddScoped<GetOrderHistoryHandler>();
        return services;
    }
}
