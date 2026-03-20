using Pitly.Core.Services;

namespace Pitly.Tests;

internal sealed class TestRateService : INbpExchangeRateService
{
    private readonly IReadOnlyDictionary<string, decimal> _rates;

    public TestRateService(IReadOnlyDictionary<string, decimal> rates)
    {
        _rates = rates;
    }

    public Task<decimal> GetRateAsync(string currency, DateTime transactionDate)
    {
        if (_rates.TryGetValue(currency, out var rate))
            return Task.FromResult(rate);

        throw new InvalidOperationException($"Missing test exchange rate for {currency}.");
    }
}
