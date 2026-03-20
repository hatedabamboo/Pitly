using Pitly.Core.Models;
using Pitly.Core.Tax;

namespace Pitly.Tests;

public class CapitalGainsTaxCalculatorTests
{
    [Fact]
    public async Task CalculateAsync_UsesIsinForFifoMatching()
    {
        var rateService = new TestRateService(new Dictionary<string, decimal>
        {
            ["USD"] = 5m
        });
        var calculator = new CapitalGainsTaxCalculator(rateService);

        var trades = new List<Trade>
        {
            new(
                Symbol: "ABC",
                Currency: "USD",
                DateTime: new DateTime(2025, 1, 2, 10, 0, 0),
                Quantity: 10m,
                Price: 10m,
                Proceeds: 100m,
                Commission: 0m,
                CommissionCurrency: "USD",
                RealizedPnL: 0m,
                Type: TradeType.Buy,
                Isin: "US1111111111"),
            new(
                Symbol: "ABC",
                Currency: "USD",
                DateTime: new DateTime(2025, 1, 3, 10, 0, 0),
                Quantity: 10m,
                Price: 20m,
                Proceeds: 200m,
                Commission: 0m,
                CommissionCurrency: "USD",
                RealizedPnL: 0m,
                Type: TradeType.Buy,
                Isin: "US2222222222"),
            new(
                Symbol: "ABC",
                Currency: "USD",
                DateTime: new DateTime(2025, 1, 4, 10, 0, 0),
                Quantity: 10m,
                Price: 30m,
                Proceeds: 300m,
                Commission: 0m,
                CommissionCurrency: "USD",
                RealizedPnL: 0m,
                Type: TradeType.Sell,
                Isin: "US2222222222")
        };

        var results = await calculator.CalculateAsync(trades);
        var sellResult = Assert.Single(results, r => r.Type == TradeType.Sell);

        Assert.Equal(1500m, sellResult.ProceedsPln);
        Assert.Equal(1000m, sellResult.CostPln);
        Assert.Equal(500m, sellResult.GainLossPln);
    }
}
