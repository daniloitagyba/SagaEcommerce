using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        services.TryAddSingleton<IPricingEngine, NRulesPricingEngine>();
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
