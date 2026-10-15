namespace StockPanel;

/// <summary>A stock from list_stocks.csv. Its price is in <see cref="Currency"/>, the purchase price in
/// <see cref="BuyCurrency"/> (the same unless the column says otherwise, as in "Purchase Price (EUR)");
/// pence and similar are already converted to the main unit.</summary>
public sealed record Holding(
    string Name, string Isin, string Ticker, string Market, string Sector,
    string Currency, double BuyPrice, double Quantity, string? Yahoo, string? BuyIn = null)
{
    public string BuyCurrency => BuyIn ?? Currency;
}

/// <summary>An index or fund from list_others.csv: only its daily trend is shown.</summary>
public sealed record IndexItem(string Name, string ShortName, string Isin, string Ticker, string? Yahoo);

public static class Lists
{
    static readonly string[] NameCols = ["Stock Name", "Name", "Stock", "Title", "Description"];
    static readonly string[] IsinCols = ["ISIN"];
    static readonly string[] TickerCols = ["Ticker", "Symbol"];
    static readonly string[] YahooCols = ["Yahoo", "Yahoo Ticker", "Yahoo Symbol"];
    static readonly string[] BuyCols = ["Purchase Price", "Buy Price", "Average Price", "Avg Price", "Average Cost", "Cost Price", "PMC", "Prezzo Medio"];
    static readonly string[] BuyCurrencyCols = ["Purchase Currency", "Buy Currency", "Valuta Acquisto"];
    static readonly string[] QtyCols = ["Quantity", "Qty", "Shares", "Units", "Quantita", "Quantità"];
    static readonly string[] PriceCols = ["Current Price", "Price", "Last Price"];
    static readonly string[] ValueCols = ["Current Total Value (Original Currency)", "Current Total Value", "Market Value", "Value"];
    static readonly string[] CurrencyCols = ["Currency", "Valuta"];
    static readonly string[] MarketCols = ["Market", "Exchange", "Mercato"];
    static readonly string[] SectorCols = ["Sector", "Settore", "Industry"];
    static readonly string[] ShortCols = ["Short name", "Short Name", "Label"];

    /// <summary>Minor currency units Yahoo and brokers use, and the main currency they belong to.</summary>
    public static (string Currency, double Factor) MainUnit(string currency) => currency switch
    {
        "GBp" or "GBX" or "GBx" => ("GBP", 0.01),
        "ILA" => ("ILS", 0.01),
        "ZAc" or "ZAC" => ("ZAR", 0.01),
        _ => (currency.ToUpperInvariant(), 1),
    };

    /// <param name="purchaseCurrency">Currency of the purchase prices (config.json "purchaseCurrency");
    /// null: each stock's own currency. A currency in the column name, as in "Purchase Price (EUR)", wins over it,
    /// and a "Purchase Currency" cell (GBP, or GBX for pence) wins over both for its own row.</param>
    public static (List<Holding> Items, List<string> Warnings) LoadStocks(string path, string? purchaseCurrency = null)
    {
        var items = new List<Holding>();
        var warnings = new List<string>();
        var table = Read(path, warnings);
        if (table is null) return (items, warnings);

        // "Purchase Price (EUR)": the purchase price is in that currency, whatever the stock trades in
        var buyColumn = table.Has(BuyCols) ? null : table.WithCurrency(BuyCols);

        foreach (var row in table.Rows)
        {
            var name = table.Get(row, NameCols);
            var ticker = table.Get(row, TickerCols);
            var isin = table.Get(row, IsinCols).ToUpperInvariant();
            if (name.Length == 0) name = ticker.Length > 0 ? ticker : isin;
            if (name.Length == 0) continue;

            var buy = buyColumn is { } bc ? table.Number(row, bc.Column) : table.Number(row, BuyCols);
            var qty = table.Number(row, QtyCols);
            if (qty is null)
            {
                // Broker exports often have no quantity: it is the position's value divided by the price.
                var value = table.Number(row, ValueCols);
                var price = table.Number(row, PriceCols);
                if (value is > 0 && price is > 0) qty = RoundQuantity(value.Value / price.Value);
            }
            if (buy is null or <= 0) warnings.Add($"{name}: no purchase price");
            if (qty is null or <= 0) warnings.Add($"{name}: no quantity");

            var (currency, factor) = MainUnit(table.Get(row, CurrencyCols) is { Length: > 0 } c ? c : "EUR");
            string? buyIn = null;
            var buyFactor = factor;
            var rowBuyCurrency = table.Get(row, BuyCurrencyCols);
            var bcur = rowBuyCurrency.Length > 0 ? rowBuyCurrency : buyColumn?.Currency ?? purchaseCurrency;
            if (bcur is { Length: > 0 }) (buyIn, buyFactor) = MainUnit(bcur.Trim());
            var yahoo = table.Get(row, YahooCols);
            items.Add(new Holding(name, isin, ticker, table.Get(row, MarketCols), table.Get(row, SectorCols),
                currency, (buy ?? 0) * buyFactor, qty ?? 0, yahoo.Length > 0 ? yahoo : null, buyIn));
        }
        return (items, warnings);
    }

    public static (List<IndexItem> Items, List<string> Warnings) LoadOthers(string path)
    {
        var items = new List<IndexItem>();
        var warnings = new List<string>();
        var table = Read(path, warnings);
        if (table is null) return (items, warnings);

        foreach (var row in table.Rows)
        {
            var name = table.Get(row, NameCols);
            var shortName = table.Get(row, ShortCols);
            var isin = table.Get(row, IsinCols).ToUpperInvariant();
            var ticker = table.Get(row, TickerCols);
            var yahoo = table.Get(row, YahooCols);
            if (shortName.Length == 0) shortName = name.Length > 0 ? name : ticker.Length > 0 ? ticker : isin;
            if (shortName.Length == 0) continue;
            items.Add(new IndexItem(name, shortName, isin, ticker, yahoo.Length > 0 ? yahoo : null));
        }
        return (items, warnings);
    }

    /// <summary>Whole shares when the division is within rounding of one, otherwise 4 decimals (fractional shares).</summary>
    internal static double RoundQuantity(double q)
    {
        var whole = Math.Round(q);
        return Math.Abs(q - whole) < 0.02 ? whole : Math.Round(q, 4);
    }

    static CsvTable? Read(string path, List<string> warnings)
    {
        if (!File.Exists(path))
        {
            warnings.Add($"{Path.GetFileName(path)} not found");
            return null;
        }
        try
        {
            return CsvTable.Parse(ReadText(path));
        }
        catch (Exception e)
        {
            warnings.Add($"{Path.GetFileName(path)} could not be read: {e.Message}");
            return null;
        }
    }

    /// <summary>UTF-8 (with or without BOM); falls back to Latin-1 for files saved as ANSI by Excel.</summary>
    static string ReadText(string path)
    {
        var bytes = ReadShared(path);
        try
        {
            return new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }
        catch (System.Text.DecoderFallbackException)
        {
            return System.Text.Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>Reads even while Excel has the file open.</summary>
    static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }
}
