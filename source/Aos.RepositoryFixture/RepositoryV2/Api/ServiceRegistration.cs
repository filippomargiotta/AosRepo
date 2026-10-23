namespace RepositoryFixture.Api;

public static class ServiceRegistration
{
    public static IServiceCollection AddPricing(this IServiceCollection services)
    {
        services.AddScoped<IQuoteService, DiscountQuoteService>();
        return services;
    }
}
