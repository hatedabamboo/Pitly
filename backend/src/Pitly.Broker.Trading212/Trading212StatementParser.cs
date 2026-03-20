using System.Globalization;
using Microsoft.Extensions.Logging;
using Pitly.Core.Models;
using Pitly.Core.Parsing;
using static Pitly.Core.Parsing.CsvHelpers;

namespace Pitly.Broker.Trading212;

public class Trading212StatementParser : IStatementParser
{
    private static readonly string[] DateTimeFormats =
        ["yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"];

    private static readonly string[] SupportedTradeFeeColumns =
    [
        "currency conversion fee",
        "stamp duty reserve tax",
        "stamp duty",
        "ptm levy",
        "transaction fee",
        "finra fee",
        "french transaction tax"
    ];

    private static readonly string[] AmbiguousTradeFeeColumns =
    [
        "charge amount"
    ];

    private readonly ILogger<Trading212StatementParser> _logger;

    public Trading212StatementParser(ILogger<Trading212StatementParser> logger)
    {
        _logger = logger;
    }

    public ParsedStatement Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new FormatException("File is empty.");

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
            throw new FormatException("File contains no data rows.");

        var headerFields = ParseCsvLine(lines[0].TrimEnd('\r'));
        var columnMap = BuildColumnMap(headerFields);

        if (!columnMap.ContainsKey("action") || !columnMap.ContainsKey("time"))
        {
            throw new FormatException(
                "File does not appear to be a Trading 212 export. Missing required columns.");
        }

        var trades = new List<Trade>();
        var dividends = new List<RawDividend>();
        var withholdingTaxes = new List<RawWithholdingTax>();

        for (int i = 1; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var fields = ParseCsvLine(line);
            var action = GetField(fields, columnMap, "action");
            if (string.IsNullOrEmpty(action))
                continue;

            var normalizedAction = NormalizeAction(action);

            if (TryGetTradeType(normalizedAction, out var tradeType))
            {
                trades.Add(ParseTrade(fields, columnMap, tradeType, lineNumber));
                continue;
            }

            if (IsDividendAction(normalizedAction))
            {
                ParseDividend(fields, columnMap, dividends, withholdingTaxes, lineNumber);
                continue;
            }

            if (ShouldIgnoreAction(normalizedAction, fields, columnMap))
                continue;

            throw new FormatException(
                $"Unsupported Trading 212 action '{action}' on line {lineNumber}. " +
                "This export contains broker events that Pitly cannot calculate safely yet.");
        }

        if (trades.Count == 0 && dividends.Count == 0)
        {
            throw new FormatException(
                "No trades or dividends found. Please upload a valid Trading 212 CSV export.");
        }

        _logger.LogInformation(
            "Parsed Trading 212 statement: {Trades} trades, {Dividends} dividends, {Withholdings} withholding tax entries",
            trades.Count, dividends.Count, withholdingTaxes.Count);

        return new ParsedStatement(trades, dividends, withholdingTaxes);
    }

    private record HeaderColumn(int Index, string RawName, string? CurrencyCode);

    private record RowFields(
        string Ticker,
        string? Isin,
        DateTime DateTime,
        decimal Shares,
        decimal PricePerShare,
        string Currency);

    private record MoneyAmount(decimal Amount, string Currency, string SourceColumn);

    private RowFields ParseCommonFields(
        List<string> fields,
        Dictionary<string, HeaderColumn> columnMap,
        string action,
        int lineNumber)
    {
        var ticker = RequireField(fields, columnMap, "ticker", action, lineNumber);
        var isin = GetField(fields, columnMap, "isin");
        var timeStr = RequireField(fields, columnMap, "time", action, lineNumber);
        var sharesStr = RequireField(fields, columnMap, "no. of shares", action, lineNumber);
        var priceStr = RequireField(fields, columnMap, "price / share", action, lineNumber);
        var currency = RequireField(fields, columnMap, "currency (price / share)", action, lineNumber);

        if (!TryParseDateTime(timeStr, out var dateTime))
        {
            throw new FormatException(
                $"Could not parse date '{timeStr}' for {action} row on line {lineNumber}.");
        }

        if (!TryParseDecimal(sharesStr, out var shares))
        {
            throw new FormatException(
                $"Could not parse share quantity '{sharesStr}' for {action} row on line {lineNumber}.");
        }

        if (!TryParseDecimal(priceStr, out var price))
        {
            throw new FormatException(
                $"Could not parse price '{priceStr}' for {action} row on line {lineNumber}.");
        }

        var (normalizedCurrency, normalizedPrice) = NormalizeMonetaryAmount(currency, price);
        return new RowFields(ticker, isin, dateTime, shares, normalizedPrice, normalizedCurrency);
    }

    private Trade ParseTrade(
        List<string> fields,
        Dictionary<string, HeaderColumn> columnMap,
        TradeType tradeType,
        int lineNumber)
    {
        var row = ParseCommonFields(fields, columnMap, "trade", lineNumber);
        if (row.Shares <= 0)
        {
            throw new FormatException(
                $"Trade row on line {lineNumber} has non-positive quantity '{row.Shares}'.");
        }

        var proceeds = row.Shares * row.PricePerShare;
        var fee = ParseTradeFees(fields, columnMap, row.Currency, lineNumber);

        return new Trade(
            Symbol: row.Ticker,
            Currency: row.Currency,
            DateTime: row.DateTime,
            Quantity: row.Shares,
            Price: row.PricePerShare,
            Proceeds: proceeds,
            Commission: fee.Amount,
            CommissionCurrency: fee.Currency,
            RealizedPnL: 0m,
            Type: tradeType,
            Isin: row.Isin);
    }

    private void ParseDividend(
        List<string> fields,
        Dictionary<string, HeaderColumn> columnMap,
        List<RawDividend> dividends,
        List<RawWithholdingTax> withholdingTaxes,
        int lineNumber)
    {
        var action = RequireField(fields, columnMap, "action", "dividend", lineNumber);
        if (action.Contains("tax free", StringComparison.OrdinalIgnoreCase) ||
            action.Contains("return of capital", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException(
                $"Unsupported Trading 212 dividend action '{action}' on line {lineNumber}. " +
                "Tax-free dividend / return-of-capital events need dedicated handling.");
        }

        var row = ParseCommonFields(fields, columnMap, "dividend", lineNumber);
        var grossAmount = row.Shares * row.PricePerShare;

        dividends.Add(new RawDividend(
            Symbol: row.Ticker,
            Currency: row.Currency,
            Date: row.DateTime.Date,
            Amount: grossAmount,
            Isin: row.Isin));

        var withholding = TryParseMoneyAmount(
            fields,
            columnMap,
            "withholding tax",
            row.Currency,
            lineNumber);

        if (withholding is not null)
        {
            withholdingTaxes.Add(new RawWithholdingTax(
                Symbol: row.Ticker,
                Currency: withholding.Currency,
                Date: row.DateTime.Date,
                Amount: withholding.Amount,
                Isin: row.Isin));
        }
    }

    private MoneyAmount ParseTradeFees(
        List<string> fields,
        Dictionary<string, HeaderColumn> columnMap,
        string tradeCurrency,
        int lineNumber)
    {
        var parsedFees = new List<MoneyAmount>();

        foreach (var feeColumn in SupportedTradeFeeColumns)
        {
            var fee = TryParseMoneyAmount(fields, columnMap, feeColumn, tradeCurrency, lineNumber);
            if (fee is not null)
                parsedFees.Add(fee);
        }

        foreach (var ambiguousColumn in AmbiguousTradeFeeColumns)
        {
            var fee = TryParseMoneyAmount(fields, columnMap, ambiguousColumn, tradeCurrency, lineNumber);
            if (fee is null)
                continue;

            if (parsedFees.Count > 0)
            {
                throw new FormatException(
                    $"Unsupported Trading 212 fee combination on line {lineNumber}: '{ambiguousColumn}' is present " +
                    "together with specific fee columns. Pitly cannot infer the correct tax treatment safely.");
            }

            parsedFees.Add(fee);
        }

        if (parsedFees.Count == 0)
            return new MoneyAmount(0m, tradeCurrency, "none");

        var distinctCurrencies = parsedFees
            .Select(f => f.Currency)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctCurrencies.Count > 1)
        {
            throw new FormatException(
                $"Trade fees on line {lineNumber} use multiple currencies ({string.Join(", ", distinctCurrencies)}). " +
                "Pitly cannot calculate that Trading 212 row safely yet.");
        }

        return new MoneyAmount(parsedFees.Sum(f => f.Amount), distinctCurrencies[0], "trade fees");
    }

    private MoneyAmount? TryParseMoneyAmount(
        List<string> fields,
        Dictionary<string, HeaderColumn> columnMap,
        string columnName,
        string defaultCurrency,
        int lineNumber)
    {
        if (!columnMap.TryGetValue(columnName, out var column))
            return null;

        var rawValue = GetField(fields, columnMap, columnName);
        if (string.IsNullOrEmpty(rawValue))
            return null;

        if (!TryParseDecimal(rawValue, out var amount))
        {
            throw new FormatException(
                $"Could not parse amount '{rawValue}' from '{column.RawName}' on line {lineNumber}.");
        }

        if (amount == 0)
            return null;

        var companionCurrencyColumn = $"currency ({columnName})";
        var currency = GetField(fields, columnMap, companionCurrencyColumn)
            ?? column.CurrencyCode
            ?? InferAccountCurrency(fields, columnMap)
            ?? defaultCurrency;

        var (normalizedCurrency, normalizedAmount) = NormalizeMonetaryAmount(currency, Math.Abs(amount));
        return new MoneyAmount(normalizedAmount, normalizedCurrency, column.RawName);
    }

    private static Dictionary<string, HeaderColumn> BuildColumnMap(List<string> headerFields)
    {
        var map = new Dictionary<string, HeaderColumn>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < headerFields.Count; i++)
        {
            var rawHeader = Clean(headerFields[i]);
            if (string.IsNullOrEmpty(rawHeader))
                continue;

            var normalizedName = NormalizeHeaderName(rawHeader);
            if (map.ContainsKey(normalizedName))
            {
                throw new FormatException(
                    $"Unsupported Trading 212 export: duplicate normalized column '{normalizedName}'.");
            }

            map[normalizedName] = new HeaderColumn(i, rawHeader, ExtractCurrencyCode(rawHeader));
        }

        return map;
    }

    private static string NormalizeHeaderName(string header)
    {
        var normalized = Clean(header);
        while (TryStripTrailingCurrencyCode(normalized, out var stripped))
            normalized = stripped;

        return string.Join(" ", normalized
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool TryStripTrailingCurrencyCode(string value, out string stripped)
    {
        stripped = value;
        var closeIndex = value.LastIndexOf(')');
        var openIndex = value.LastIndexOf('(');
        if (openIndex < 0 || closeIndex != value.Length - 1 || openIndex >= closeIndex)
            return false;

        var token = value[(openIndex + 1)..closeIndex].Trim();
        if (token.Length != 3 || !token.All(char.IsLetter))
            return false;

        stripped = value[..openIndex].TrimEnd();
        return true;
    }

    private static string? ExtractCurrencyCode(string header)
    {
        var closeIndex = header.LastIndexOf(')');
        var openIndex = header.LastIndexOf('(');
        if (openIndex < 0 || closeIndex != header.Length - 1 || openIndex >= closeIndex)
            return null;

        var token = header[(openIndex + 1)..closeIndex].Trim();
        return token.Length == 3 && token.All(char.IsLetter)
            ? token.ToUpperInvariant()
            : null;
    }

    private static string? GetField(
        List<string> fields,
        Dictionary<string, HeaderColumn> columnMap,
        string columnName)
    {
        if (!columnMap.TryGetValue(columnName, out var column) || column.Index >= fields.Count)
            return null;

        var value = Clean(fields[column.Index]);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string RequireField(
        List<string> fields,
        Dictionary<string, HeaderColumn> columnMap,
        string columnName,
        string action,
        int lineNumber)
    {
        return GetField(fields, columnMap, columnName)
            ?? throw new FormatException(
                $"Missing required Trading 212 field '{columnName}' for {action} row on line {lineNumber}.");
    }

    private static bool TryParseDateTime(string s, out DateTime result)
    {
        return DateTime.TryParseExact(
            s.Trim(),
            DateTimeFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out result);
    }

    private static (string Currency, decimal Amount) NormalizeMonetaryAmount(string currency, decimal amount)
        => currency.Equals("GBX", StringComparison.OrdinalIgnoreCase)
            ? ("GBP", amount / 100m)
            : (currency.ToUpperInvariant(), amount);

    private static string NormalizeAction(string action)
        => string.Join(" ", Clean(action)
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static bool TryGetTradeType(string action, out TradeType tradeType)
    {
        if (ContainsWord(action, "buy"))
        {
            tradeType = TradeType.Buy;
            return true;
        }

        if (ContainsWord(action, "sell"))
        {
            tradeType = TradeType.Sell;
            return true;
        }

        tradeType = default;
        return false;
    }

    private static bool IsDividendAction(string action)
        => action.StartsWith("dividend", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsWord(string action, string word)
    {
        var tokens = action.Split(
            [' ', '-', '/', '(', ')'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return tokens.Contains(word, StringComparer.OrdinalIgnoreCase);
    }

    private static bool ShouldIgnoreAction(
        string action,
        List<string> fields,
        Dictionary<string, HeaderColumn> columnMap)
    {
        if (action is "deposit" or "withdrawal")
            return true;

        if (HasSecurityFootprint(fields, columnMap))
            return false;

        return !HasEconomicFootprint(fields, columnMap);
    }

    private static bool HasSecurityFootprint(
        List<string> fields,
        Dictionary<string, HeaderColumn> columnMap)
    {
        return !string.IsNullOrEmpty(GetField(fields, columnMap, "ticker")) ||
               !string.IsNullOrEmpty(GetField(fields, columnMap, "isin")) ||
               !string.IsNullOrEmpty(GetField(fields, columnMap, "no. of shares")) ||
               !string.IsNullOrEmpty(GetField(fields, columnMap, "price / share"));
    }

    private static bool HasEconomicFootprint(
        List<string> fields,
        Dictionary<string, HeaderColumn> columnMap)
    {
        foreach (var columnName in SupportedTradeFeeColumns.Concat(AmbiguousTradeFeeColumns))
        {
            if (HasNonZeroAmount(fields, columnMap, columnName))
                return true;
        }

        return HasNonZeroAmount(fields, columnMap, "total") ||
               HasNonZeroAmount(fields, columnMap, "result") ||
               HasNonZeroAmount(fields, columnMap, "withholding tax");
    }

    private static bool HasNonZeroAmount(
        List<string> fields,
        Dictionary<string, HeaderColumn> columnMap,
        string columnName)
    {
        var value = GetField(fields, columnMap, columnName);
        return TryParseDecimal(value, out var amount) && amount != 0;
    }

    private static string? InferAccountCurrency(
        List<string> fields,
        Dictionary<string, HeaderColumn> columnMap)
    {
        return GetField(fields, columnMap, "currency (total)") ??
               GetField(fields, columnMap, "currency (result)") ??
               GetField(fields, columnMap, "currency (currency conversion fee)") ??
               columnMap.GetValueOrDefault("total")?.CurrencyCode ??
               columnMap.GetValueOrDefault("result")?.CurrencyCode ??
               columnMap.GetValueOrDefault("currency conversion fee")?.CurrencyCode;
    }
}
