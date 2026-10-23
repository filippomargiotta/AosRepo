using System.Globalization;

namespace RepositoryFixture.Pricing;

public static class PricingTests
{
    public static int RunFocused()
    {
        const decimal expected = 80m;
        var actual = PriceCalculator.ApplyDiscount(100m, 0.20m);
        Console.WriteLine($"{(actual == expected ? "PASS" : "FAIL")} PricingTests.ApplyDiscount_ReturnsRemainingPrice: expected {expected.ToString(CultureInfo.InvariantCulture)}, actual {actual.ToString(CultureInfo.InvariantCulture)}");
        return actual == expected ? 0 : 1;
    }
}
