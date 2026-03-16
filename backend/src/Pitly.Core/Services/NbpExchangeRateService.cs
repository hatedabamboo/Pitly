using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Pitly.Core.Services;

public class NbpExchangeRateService : INbpExchangeRateService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<NbpExchangeRateService> _logger;
    private readonly ConcurrentDictionary<string, decimal> _cache = new();

    public NbpExchangeRateService(HttpClient httpClient, ILogger<NbpExchangeRateService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<decimal> GetRateAsync(string currency, DateTime transactionDate)
    {
        if (currency.Equals("PLN", StringComparison.OrdinalIgnoreCase))
            return 1m;

        // Polish tax law: rate from last business day BEFORE the transaction date
        var rateDate = transactionDate.Date.AddDays(-1);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            var dateStr = rateDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var cacheKey = $"{currency.ToUpperInvariant()}_{dateStr}";

            if (_cache.TryGetValue(cacheKey, out var cached))
                return cached;

            try
            {
                var url = $"https://api.nbp.pl/api/exchangerates/rates/A/{currency.ToUpperInvariant()}/{dateStr}/?format=json";
                var response = await _httpClient.GetAsync(url);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogDebug("NBP rate not found for {Currency} on {Date}, trying previous day", currency, dateStr);
                    rateDate = rateDate.AddDays(-1);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var rate = doc.RootElement
                    .GetProperty("rates")[0]
                    .GetProperty("mid")
                    .GetDecimal();

                _cache.TryAdd(cacheKey, rate);
                _logger.LogDebug("NBP rate for {Currency} on {Date}: {Rate}", currency, dateStr, rate);
                return rate;
            }
            catch (HttpRequestException ex) when (attempt < 4)
            {
                _logger.LogWarning(ex, "NBP API request failed for {Currency} on {Date} (attempt {Attempt}/5), retrying",
                    currency, dateStr, attempt + 1);
                rateDate = rateDate.AddDays(-1);
            }
        }

        _logger.LogError("Failed to get NBP rate for {Currency} near {Date} after 5 attempts", currency, transactionDate);
        throw new InvalidOperationException(
            $"Could not find NBP exchange rate for {currency} near {transactionDate:yyyy-MM-dd} after 5 attempts.");
    }
}
