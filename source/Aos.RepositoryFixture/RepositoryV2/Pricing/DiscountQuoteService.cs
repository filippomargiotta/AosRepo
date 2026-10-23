namespace RepositoryFixture.Pricing;

public interface IQuoteService
{
    decimal Quote(decimal price, decimal discount);
}

public sealed class DiscountQuoteService : IQuoteService
{
    public decimal Quote(decimal price, decimal discount) => PriceCalculator.ApplyDiscount(price, discount);
}
