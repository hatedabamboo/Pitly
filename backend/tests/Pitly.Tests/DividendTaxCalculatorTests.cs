using Pitly.Core.Models;
using Pitly.Core.Tax;

namespace Pitly.Tests;

public class DividendTaxCalculatorTests
{
    [Fact]
    public async Task CalculateAsync_SumsMatchingWithholdingRowsForSameDividend()
    {
        var rateService = new TestRateService(new Dictionary<string, decimal>
        {
            ["USD"] = 4m
        });
        var calculator = new DividendTaxCalculator(rateService);

        var dividends = new List<RawDividend>
        {
            new("META", "USD", new DateTime(2025, 12, 23), 10m, "US30303M1027")
        };
        var withholdingTaxes = new List<RawWithholdingTax>
        {
            new("META", "USD", new DateTime(2025, 12, 23), 1.0m, "US30303M1027"),
            new("META", "USD", new DateTime(2025, 12, 23), 0.5m, "US30303M1027")
        };

        var results = await calculator.CalculateAsync(dividends, withholdingTaxes);
        var dividend = Assert.Single(results);

        Assert.Equal(10m, dividend.AmountOriginal);
        Assert.Equal(1.5m, dividend.WithholdingTaxOriginal);
        Assert.Equal(40m, dividend.AmountPln);
        Assert.Equal(6m, dividend.WithholdingTaxPln);
    }
}
