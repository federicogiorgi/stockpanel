using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockPanel;

/// <summary>A price line for the page: T in unix seconds, V the values. Ref is the dashed reference level
/// (the previous close); X0..X1 the time span the chart covers (the trading session).</summary>
public sealed record SeriesDto(long[] T, double[] V, double? Ref, long? X0, long? X1);

/// <summary>The market's current or next regular session (unix seconds); the page compares it with the clock
/// to show open and closed markets, and when a closed one opens.</summary>
public sealed record SessionDto(long Start, long End);

public sealed record StockDto(
    string Name, string Sector, string? Symbol, string Currency, double? Price, double? ChangePct,
    double? OverallPct, double? Pl, double? Day, double? Value, bool Open, bool Stale, SeriesDto? Intraday, SessionDto? Session,
    string? QuoteUnit = null);

public sealed record IndexDto(string Name, string? Symbol, double? Price, double? ChangePct, bool Open, bool Stale, SeriesDto? Intraday, SessionDto? Session);

public sealed record FxDto(string Pair, double Rate, double? ChangePct);

/// <summary>A tile for a spare grid cell (Bitcoin, the dollar): price with its own decimals and currency.</summary>
public sealed record ExtraDto(string Name, string Note, double Price, int Decimals, string? Currency, double? ChangePct,
    bool Open, bool Stale, SeriesDto? Intraday, SessionDto? Session);

public sealed record Snapshot(
    string Updated, string? DataTime, string Base, WeatherNow? Weather,
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
    public DateTime NextResolveUtc { get; set; }
}

public static class SnapshotBuilder
{
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
            var d = s.Day is null ? null : Today(s.Day, nowUnix);
            // everything in the base currency at today's rates; the purchase price may be in another
            // currency than the stock (a USD stock bought for EUR), which then includes the currency move
            var rate = Rate(h.Currency);
            var buyRate = Rate(h.BuyCurrency);
            double? price = d?.Price, prev = d?.PrevClose;
            double? priceBase = price / rate;
            double? buyBase = h.BuyPrice > 0 ? h.BuyPrice / buyRate : null;
            double? change = price is not null && prev is > 0 ? (price / prev - 1) * 100 : null;
            double? overall = priceBase / buyBase * 100 - 100;
            double? pl = (priceBase - buyBase) * h.Quantity;
            double? day = price is not null && prev is not null ? (price - prev) / rate * h.Quantity : null;
            double? value = priceBase * h.Quantity;
            foreach (var c in new[] { h.Currency, h.BuyCurrency })
                if (d is not null && Rate(c) is null) Log.Once("fx:" + c, $"No exchange rate for {c}");

            stockDtos.Add((new StockDto(h.Name, h.Sector, s.Symbol, h.Currency, R(price, 4), R(change, 2), R(overall, 2),
                R(pl, 2), R(day, 2), R(value, 2), d?.IsOpen ?? false, Stale(s),
                d is null ? null : Intraday(d), Session(d), d?.QuoteUnit), k));
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
            var d = i.Day is null ? null : Today(i.Day, nowUnix);
            double? change = d is not null && d.PrevClose is > 0 ? (d.Price / d.PrevClose - 1) * 100 : null;
            return new IndexDto(i.Index!.ShortName, i.Symbol, R(d?.Price, 4), R(change, 2), d?.IsOpen ?? false, Stale(i),
                d is null ? null : Intraday(d), Session(d));
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

        return new Snapshot(now.ToString("o"), lastDataUtc?.ToString("o"), cfg.BaseCurrency, weather,
            indexDtos, sorted.Select(x => x.Dto).ToList(), fxDto, extras, warnings, false);

        ExtraDto Extra(string name, string note, ChartData d, int decimals, string? currency, DateTimeOffset at) =>
            new(name, note, Math.Round(d.Price, decimals), decimals, currency,
                d.PrevClose is > 0 ? R((d.Price / d.PrevClose - 1) * 100, 2) : null, d.IsOpen,
                at.UtcDateTime - DateTimeOffset.FromUnixTimeSeconds(d.MarketTime).UtcDateTime > TimeSpan.FromDays(4), Intraday(d), Session(d));
    }

    /// <summary>Right after the open Yahoo still reports yesterday's last trade (its European quotes come ~15 minutes
    /// late), sometimes with yesterday's previous close too, which would repeat yesterday's move. Until today's first
    /// trade arrives the day's change is 0 from the last price, and the chart starts empty at today's open.</summary>
    internal static ChartData Today(ChartData d, long nowUnix) =>
        d.RegularStart is { } start && nowUnix >= start && d.MarketTime < start
            ? d with { PrevClose = d.Price, T = [start], C = [d.Price], PeriodStart = start, PeriodEnd = d.RegularEnd ?? d.PeriodEnd }
            : d;

    static SessionDto? Session(ChartData? d) =>
        d is { RegularStart: { } s, RegularEnd: { } e } && e > s ? new SessionDto(s, e) : null;

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

    static double? R(double? v, int digits) => v is { } x && double.IsFinite(x) ? Math.Round(x, digits) : null;
}
