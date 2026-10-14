using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockPanel;

/// <summary>A price line for the page: T in unix seconds, V the values. Ref is the dashed reference level
/// (previous close or purchase price); X0..X1 the time span the chart covers (the trading session).</summary>
public sealed record SeriesDto(long[] T, double[] V, double? Ref, long? X0, long? X1);

public sealed record StockDto(
    string Name, string Sector, string? Symbol, string Currency, double? Price, double? ChangePct,
    double? OverallPct, double? Pl, double? Day, double? Value, bool Open, bool Stale,
    SeriesDto? Intraday, SeriesDto? History);

public sealed record IndexDto(string Name, string? Symbol, double? Price, double? ChangePct, bool Open, bool Stale, SeriesDto? Intraday);

public sealed record PortfolioDto(double Value, double Day, double? DayPct, double Pl, double? PlPct, double Cost,
    SeriesDto? Intraday, SeriesDto? History);

public sealed record FxDto(string Pair, double Rate, double? ChangePct);

/// <summary>A tile for a spare grid cell (Bitcoin, the dollar): price with its own decimals and currency.</summary>
public sealed record ExtraDto(string Name, string Note, double Price, int Decimals, string? Currency, double? ChangePct,
    bool Open, bool Stale, SeriesDto? Intraday);

public sealed record Snapshot(
    string Updated, string? DataTime, string Base, string HistLabel, WeatherNow? Weather, PortfolioDto? Portfolio,
    List<IndexDto> Indices, List<StockDto> Stocks, FxDto? Fx, List<ExtraDto> Extras, List<string> Warnings, bool Loading)
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>A stock or index with its latest data from Yahoo.</summary>
public sealed class Instrument
{
    public Holding? Holding { get; init; }
    public IndexItem? Index { get; init; }
    public string? Symbol { get; set; }
    public ChartData? Day { get; set; }
    public DateTime DayFetchedUtc { get; set; }
    public ChartData? History { get; set; }
    public DateTime NextResolveUtc { get; set; }
}

public static class SnapshotBuilder
{
    public static string HistoryLabel(string range) => range.ToLowerInvariant() switch
    {
        "1mo" => "1M", "3mo" => "3M", "6mo" => "6M", "1y" => "1Y", "2y" => "2Y", "5y" => "5Y", "10y" => "10Y",
        "ytd" => "YTD", "max" => "MAX", var r => r.ToUpperInvariant(),
    };

    public static Snapshot Build(PanelConfig cfg, IReadOnlyList<Instrument> stocks, IReadOnlyList<Instrument> indices,
        IReadOnlyDictionary<string, ChartData> fx, ChartData? btc, WeatherNow? weather, List<string> warnings, DateTime? lastDataUtc, DateTimeOffset now)
    {
        var nowUnix = now.ToUnixTimeSeconds();
        var staleAfter = TimeSpan.FromSeconds(cfg.RefreshSeconds * 3 + 30);
        bool Stale(Instrument i) => i.Day is null || now.UtcDateTime - i.DayFetchedUtc > staleAfter;

        double? Rate(string currency) =>
            string.Equals(currency, cfg.BaseCurrency, StringComparison.OrdinalIgnoreCase) ? 1
            : fx.TryGetValue(currency, out var c) && c.Price > 0 ? c.Price : null;

        var stockDtos = new List<(StockDto Dto, int Order)>();
        for (var k = 0; k < stocks.Count; k++)
        {
            var s = stocks[k];
            var h = s.Holding!;
            var d = s.Day;
            var rate = Rate(h.Currency);
            double? price = d?.Price, prev = d?.PrevClose;
            double? change = price is not null && prev is > 0 ? (price / prev - 1) * 100 : null;
            double? overall = price is not null && h.BuyPrice > 0 ? (price / h.BuyPrice - 1) * 100 : null;
            double? pl = price is not null && rate is not null ? (price - h.BuyPrice) * h.Quantity / rate : null;
            double? day = price is not null && prev is not null && rate is not null ? (price - prev) * h.Quantity / rate : null;
            double? value = price is not null && rate is not null ? price * h.Quantity / rate : null;
            if (d is not null && rate is null) Log.Once("fx:" + h.Currency, $"No exchange rate for {h.Currency}");

            stockDtos.Add((new StockDto(h.Name, h.Sector, s.Symbol, h.Currency, R(price, 4), R(change, 2), R(overall, 2),
                R(pl, 2), R(day, 2), R(value, 2), d?.IsOpen ?? false, Stale(s),
                d is null ? null : Intraday(d), History(s.History, d, h.BuyPrice)), k));
        }

        var sorted = cfg.SortBy.ToLowerInvariant() switch
        {
            "name" => stockDtos.OrderBy(x => x.Dto.Name, StringComparer.OrdinalIgnoreCase),
            "daily" => stockDtos.OrderByDescending(x => x.Dto.ChangePct ?? double.MinValue),
            "overall" => stockDtos.OrderByDescending(x => x.Dto.OverallPct ?? double.MinValue),
            "value" => stockDtos.OrderByDescending(x => x.Dto.Value ?? double.MinValue),
            _ => stockDtos.OrderBy(x => x.Order),
        };

        var indexDtos = indices.Select(i =>
        {
            var d = i.Day;
            double? change = d is not null && d.PrevClose is > 0 ? (d.Price / d.PrevClose - 1) * 100 : null;
            return new IndexDto(i.Index!.ShortName, i.Symbol, R(d?.Price, 4), R(change, 2), d?.IsOpen ?? false, Stale(i),
                d is null ? null : Intraday(d));
        }).ToList();

        FxDto? fxDto = null;
        var fxCur = fx.ContainsKey("USD") ? "USD" : fx.Keys.FirstOrDefault();
        if (fxCur is not null && fx[fxCur] is var fc)
            fxDto = new FxDto($"{cfg.BaseCurrency}/{fxCur}", Math.Round(fc.Price, 4),
                fc.PrevClose is > 0 ? R((fc.Price / fc.PrevClose - 1) * 100, 2) : null);

        // spare grid cells, in this order: Bitcoin in dollars, then the dollar in the base currency
        var extras = new List<ExtraDto>();
        if (btc is not null)
            extras.Add(Extra("BTC", "Bitcoin", btc, 0, "USD", now));
        if (fx.TryGetValue("USD", out var usd) && usd.Price > 0)
            extras.Add(Extra($"USD/{cfg.BaseCurrency}", "exchange rate", Invert(usd), 4, null, now));

        return new Snapshot(now.ToString("o"), lastDataUtc?.ToString("o"), cfg.BaseCurrency, HistoryLabel(cfg.HistoryRange), weather,
            Portfolio(stocks, Rate, nowUnix), indexDtos, sorted.Select(x => x.Dto).ToList(), fxDto, extras, warnings, false);

        ExtraDto Extra(string name, string note, ChartData d, int decimals, string? currency, DateTimeOffset at) =>
            new(name, note, Math.Round(d.Price, decimals), decimals, currency,
                d.PrevClose is > 0 ? R((d.Price / d.PrevClose - 1) * 100, 2) : null, d.IsOpen,
                at.UtcDateTime - DateTimeOffset.FromUnixTimeSeconds(d.MarketTime).UtcDateTime > TimeSpan.FromDays(4), Intraday(d));
    }

    /// <summary>The same quote the other way round (EUR/USD to USD/EUR).</summary>
    internal static ChartData Invert(ChartData d) => d with
    {
        Price = 1 / d.Price,
        PrevClose = d.PrevClose is > 0 ? 1 / d.PrevClose : null,
        C = d.C.Select(v => 1 / v).ToArray(),
    };

    /// <summary>The day's 5-minute closes, ending at the latest price, over the whole trading session.</summary>
    internal static SeriesDto Intraday(ChartData d)
    {
        var t = d.T.ToList();
        var v = d.C.ToList();
        if (t.Count == 0 || d.MarketTime > t[^1]) { t.Add(d.MarketTime); v.Add(d.Price); }
        else v[^1] = d.Price;
        var x0 = d.PeriodStart ?? t[0];
        var x1 = d.PeriodEnd ?? t[^1];
        if (t[0] < x0) x0 = t[0];
        if (t[^1] > x1) x1 = t[^1];
        return new SeriesDto(t.ToArray(), v.Select(x => Math.Round(x, 4)).ToArray(), R(d.PrevClose ?? v[0], 4), x0, x1);
    }

    /// <summary>Daily closes of the long chart; the last one is replaced by the live price.</summary>
    internal static SeriesDto? History(ChartData? h, ChartData? day, double buy)
    {
        if (h is null || h.T.Length == 0) return null;
        var t = h.T.ToList();
        var v = h.C.ToList();
        if (day is not null)
        {
            if (day.MarketTime - t[^1] < 20 * 3600) v[^1] = day.Price;
            else { t.Add(day.MarketTime); v.Add(day.Price); }
        }
        return new SeriesDto(t.ToArray(), v.Select(x => Math.Round(x, 4)).ToArray(), buy > 0 ? Math.Round(buy, 4) : null, null, null);
    }

    static PortfolioDto? Portfolio(IReadOnlyList<Instrument> stocks, Func<string, double?> rate, long nowUnix)
    {
        // Positions with a price and an exchange rate; units = quantity / rate, so value = price * units in base currency.
        var pos = stocks
            .Where(s => s.Day is not null && s.Holding!.Quantity > 0 && rate(s.Holding.Currency) is not null)
            .Select(s => (s.Holding!, s.Day!, s.History, Units: s.Holding!.Quantity / rate(s.Holding.Currency)!.Value))
            .ToList();
        if (pos.Count == 0) return null;

        double value = 0, prevValue = 0, cost = 0;
        foreach (var (h, d, _, u) in pos)
        {
            value += d.Price * u;
            prevValue += (d.PrevClose ?? d.Price) * u;
            cost += h.BuyPrice * u;
        }
        var day = value - prevValue;
        var pl = value - cost;
        return new PortfolioDto(Math.Round(value, 2), Math.Round(day, 2), prevValue > 0 ? R(day / prevValue * 100, 2) : null,
            Math.Round(pl, 2), cost > 0 ? R(pl / cost * 100, 2) : null, Math.Round(cost, 2),
            PortfolioIntraday(pos.Select(p => (p.Item2, p.Units)).ToList(), prevValue, nowUnix),
            PortfolioHistory(pos.Select(p => (p.History, p.Item2, p.Units)).ToList(), cost));
    }

    /// <summary>Portfolio value through today's sessions (Europe, then US) on a 5-minute grid. Positions whose
    /// latest session is an earlier day (a market closed today) count at their last price throughout.</summary>
    internal static SeriesDto? PortfolioIntraday(List<(ChartData Day, double Units)> pos, double prevValue, long nowUnix)
    {
        static long DayOf(ChartData d) => (d.PeriodStart ?? (d.T.Length > 0 ? d.T[0] : d.MarketTime)) / 86400;
        var live = pos.Where(p => p.Day.T.Length > 0).ToList();
        if (live.Count == 0) return null;
        var today = live.Max(p => DayOf(p.Day));
        var current = live.Where(p => DayOf(p.Day) == today).ToList();
        var x0 = current.Min(p => Math.Min(p.Day.PeriodStart ?? p.Day.T[0], p.Day.T[0]));
        var x1 = current.Max(p => Math.Max(p.Day.PeriodEnd ?? p.Day.T[^1], p.Day.T[^1]));
        var lastData = current.Max(p => Math.Max(p.Day.T[^1], Math.Min(p.Day.MarketTime, p.Day.PeriodEnd ?? long.MaxValue)));
        var end = Math.Min(Math.Max(lastData, x0), Math.Min(nowUnix, x1));

        var grid = new List<long>();
        for (var t = x0; t < end; t += 300) grid.Add(t);
        grid.Add(end);

        var values = new double[grid.Count];
        foreach (var (d, u) in pos)
        {
            var isCurrent = DayOf(d) == today && d.T.Length > 0;
            var j = -1;
            for (var g = 0; g < grid.Count; g++)
            {
                double p;
                if (!isCurrent) p = d.Price;
                else
                {
                    while (j + 1 < d.T.Length && d.T[j + 1] <= grid[g]) j++;
                    p = g == grid.Count - 1 && grid[g] >= d.T[^1] ? d.Price : j >= 0 ? d.C[j] : d.PrevClose ?? d.C[0];
                }
                values[g] += p * u;
            }
        }
        return new SeriesDto(grid.ToArray(), values.Select(v => Math.Round(v, 2)).ToArray(), Math.Round(prevValue, 2), x0, x1);
    }

    /// <summary>Value of today's holdings over the long range (the quantities are assumed unchanged).</summary>
    internal static SeriesDto? PortfolioHistory(List<(ChartData? History, ChartData Day, double Units)> pos, double cost)
    {
        var withHist = pos.Where(p => p.History is { T.Length: > 0 }).ToList();
        if (withHist.Count == 0) return null;
        // live price replaces the last close, as in the stock tiles
        var series = withHist.Select(p => (S: History(p.History, p.Day, 0)!, p.Units)).ToList();
        var start = series.Max(s => s.S.T[0] / 86400);
        var days = series.SelectMany(s => s.S.T.Select(t => t / 86400)).Where(d => d >= start).Distinct().Order().ToList();

        var values = new double[days.Count];
        foreach (var (s, u) in series)
        {
            var j = -1;
            for (var k = 0; k < days.Count; k++)
            {
                while (j + 1 < s.T.Length && s.T[j + 1] / 86400 <= days[k]) j++;
                values[k] += (j >= 0 ? s.V[j] : s.V[0]) * u;
            }
        }
        return new SeriesDto(days.Select(d => d * 86400).ToArray(), values.Select(v => Math.Round(v, 2)).ToArray(),
            cost > 0 ? Math.Round(cost, 2) : null, null, null);
    }

    static double? R(double? v, int digits) => v is { } x && double.IsFinite(x) ? Math.Round(x, digits) : null;
}
