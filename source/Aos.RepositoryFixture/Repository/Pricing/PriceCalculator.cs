namespace RepositoryFixture.Pricing;

public static class PriceCalculator
{
    // Deliberate diagnostic fixture defect: returns the discount instead of the remaining price.
    public static decimal ApplyDiscount(decimal price, decimal discount) => price * discount;
}
