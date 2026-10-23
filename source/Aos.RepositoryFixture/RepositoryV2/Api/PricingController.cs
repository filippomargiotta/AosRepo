namespace RepositoryFixture.Api;

[ApiController]
[Route("api/pricing")]
public sealed class PricingController(IQuoteService quotes) : ControllerBase
{
    // Reported behavior: GET /api/pricing/quote returns the discount amount instead of the remaining price.
    [HttpGet("quote")]
    public decimal Quote(decimal price, decimal discount) => quotes.Quote(price, discount);
}
