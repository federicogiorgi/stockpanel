using System.Text.Json;
using System.Text.RegularExpressions;

namespace StockPanel;

/// <summary>Turns broker tickers (Reuters style: AAPL.O, KO.N, SAP.EQ, SIE.FRA) into Yahoo Finance symbols.</summary>
public static class SymbolRules
{
    // Reuters suffixes of US venues
    static readonly HashSet<string> UsSuffixes = new(StringComparer.OrdinalIgnoreCase) { "O", "OQ", "N", "A", "K", "P", "PK", "OB", "Z" };

    // Multi-venue platforms: the primary listing follows the ISIN's country
    static readonly HashSet<string> MultiVenue = new(StringComparer.OrdinalIgnoreCase) { "EQ", "TQ", "CHI", "BS", "TRE" };

    static readonly Dictionary<string, string> CountrySuffix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DE"] = ".DE", ["FR"] = ".PA", ["IT"] = ".MI", ["NL"] = ".AS", ["ES"] = ".MC", ["BE"] = ".BR",
        ["PT"] = ".LS", ["FI"] = ".HE", ["IE"] = ".IR", ["AT"] = ".VI", ["DK"] = ".CO", ["SE"] = ".ST",
        ["NO"] = ".OL", ["CH"] = ".SW", ["GB"] = ".L", ["US"] = "", ["CA"] = ".TO",
    };

    static readonly Dictionary<string, string> MarketSuffix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NASDAQ"] = "", ["NYSE"] = "", ["NYSE ARCA"] = "", ["AMEX"] = "", ["NYSE AMERICAN"] = "",
        ["XETRA"] = ".DE", ["FRANKFURT"] = ".F", ["FRA"] = ".F", ["STUTTGART"] = ".SG",
        ["MTA"] = ".MI", ["MIL"] = ".MI", ["BIT"] = ".MI", ["BORSA ITALIANA"] = ".MI", ["EURONEXT MILAN"] = ".MI", ["ETFPLUS"] = ".MI",
        ["PARIS"] = ".PA", ["EURONEXT PARIS"] = ".PA", ["AMSTERDAM"] = ".AS", ["EURONEXT AMSTERDAM"] = ".AS",
        ["BRUSSELS"] = ".BR", ["EURONEXT BRUSSELS"] = ".BR", ["LISBON"] = ".LS", ["MADRID"] = ".MC", ["BME"] = ".MC",
        ["LSE"] = ".L", ["LONDON"] = ".L", ["SIX"] = ".SW", ["SWISS"] = ".SW", ["VIENNA"] = ".VI",
        ["COPENHAGEN"] = ".CO", ["STOCKHOLM"] = ".ST", ["OSLO"] = ".OL", ["HELSINKI"] = ".HE", ["TORONTO"] = ".TO", ["TSX"] = ".TO",
    };

    // Reuters suffixes that Yahoo spells differently; the rest (DE, PA, MI, AS, L, ...) are the same
    static readonly Dictionary<string, string> ReutersSuffix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FRA"] = ".F", ["S"] = ".SW", ["TO"] = ".TO", ["MC"] = ".MC", ["DE"] = ".DE", ["PA"] = ".PA", ["MI"] = ".MI",
        ["AS"] = ".AS", ["BR"] = ".BR", ["LS"] = ".LS", ["HE"] = ".HE", ["IR"] = ".IR", ["VI"] = ".VI", ["CO"] = ".CO",
        ["ST"] = ".ST", ["OL"] = ".OL", ["SW"] = ".SW", ["L"] = ".L", ["F"] = ".F", ["SG"] = ".SG", ["HK"] = ".HK",
        ["T"] = ".T", ["AX"] = ".AX",
    };

    public static string? FromTicker(string ticker, string isin, string market)
    {
        ticker = ticker.Trim();
        if (ticker.Length == 0) return null;
        var dot = ticker.LastIndexOf('.');
        var root = Root(dot > 0 ? ticker[..dot] : ticker);
        var suffix = dot > 0 ? ticker[(dot + 1)..] : "";

        if (MarketSuffix.TryGetValue(market.Trim(), out var ms)) return root + ms;
        if (suffix.Length == 0 || UsSuffixes.Contains(suffix)) return root;
        if (MultiVenue.Contains(suffix))
            return isin.Length >= 2 && CountrySuffix.TryGetValue(isin[..2], out var cs) ? root + cs : null;
        return ReutersSuffix.TryGetValue(suffix, out var rs) ? root + rs : null;
    }

    /// <summary>BRKb / BRK/B / BRK.B share classes are BRK-B on Yahoo.</summary>
    static string Root(string root)
    {
        root = root.Replace('/', '-');
        var m = Regex.Match(root, "^([A-Z0-9]+)([a-z])$");
        return m.Success ? $"{m.Groups[1].Value}-{m.Groups[2].Value.ToUpperInvariant()}" : root.ToUpperInvariant();
    }

    // Euro listings first, Borsa Italiana and Xetra before the others; thin regional German venues last.
    static readonly string[] ExchangeOrder = ["MIL", "GER", "AMS", "PAR", "BRU", "MCE", "LIS", "VIE", "FRA", "EBS", "LSE", "NMS", "NYQ", "NGM", "NCM", "PCX"];

    public static IEnumerable<SearchHit> Rank(IEnumerable<SearchHit> hits) =>
        hits.Select((h, i) => (h, i))
            .OrderBy(x => Array.IndexOf(ExchangeOrder, x.h.Exchange) is var k && k >= 0 ? k : ExchangeOrder.Length)
            .ThenBy(x => x.i)
            .Select(x => x.h);
}

/// <summary>What to look up: explicit symbols are trusted, the rest are checked against the expected currency.</summary>
public sealed record SymbolQuery(string Key, string Name, string Isin, string Ticker, string Market, string? Explicit, string? Currency);

/// <summary>Finds the Yahoo symbol of a stock or fund and remembers it in %LOCALAPPDATA%\stockpanel\symbols.json.</summary>
public sealed class SymbolResolver
{
    readonly YahooClient _yahoo;
    readonly IReadOnlyDictionary<string, string> _overrides;
    readonly Dictionary<string, string> _cache;
    readonly object _gate = new();
    bool _dirty;

    public SymbolResolver(YahooClient yahoo, IReadOnlyDictionary<string, string> overrides)
    {
        _yahoo = yahoo;
        _overrides = overrides;
        _cache = LoadCache();
    }

    public async Task<(string? Symbol, ChartData? Chart)> ResolveAsync(SymbolQuery q, CancellationToken ct)
    {
        // 1. config.json "symbols" or a Yahoo column in the CSV: used as given
        var trusted = Override(q) ?? q.Explicit;
        if (trusted is not null) return (trusted, await TryChart(trusted, ct));

        // 2. remembered, 3. derived from the ticker, 4. search by ISIN (then by ticker)
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        (string Symbol, ChartData Chart)? fallback = null;
        async Task<(string, ChartData)?> Check(string? symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol) || !tried.Add(symbol)) return null;
            var chart = await TryChart(symbol, ct);
            if (chart is null) return null;
            if (q.Currency is null || string.Equals(chart.Currency, q.Currency, StringComparison.OrdinalIgnoreCase)) return (symbol, chart);
            fallback ??= (symbol, chart);
            return null;
        }

        string? cached;
        lock (_gate) _cache.TryGetValue(q.Key, out cached);
        var found = await Check(cached) ?? await Check(SymbolRules.FromTicker(q.Ticker, q.Isin, q.Market));
        foreach (var term in new[] { q.Isin, q.Ticker })
        {
            if (found is not null || term.Length == 0) break;
            List<SearchHit> hits;
            try { hits = await _yahoo.SearchAsync(term, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { Log.Once("search:" + term, $"Yahoo search for {term} failed: {e.Message}"); continue; }
            foreach (var hit in SymbolRules.Rank(hits).Take(6))
                if ((found = await Check(hit.Symbol)) is not null) break;
        }
        found ??= fallback;
        if (found is null) return (null, null);

        var (sym, ch) = found.Value;
        if (q.Currency is not null && !string.Equals(ch.Currency, q.Currency, StringComparison.OrdinalIgnoreCase))
            Log.Once("cur:" + q.Key, $"{q.Name}: {sym} trades in {ch.Currency}, the list says {q.Currency}; add the right symbol to config.json");
        lock (_gate)
        {
            if (!_cache.TryGetValue(q.Key, out var old) || old != sym) { _cache[q.Key] = sym; _dirty = true; }
        }
        if (cached != sym) Log.Info($"{q.Name}: using Yahoo symbol {sym}");
        return (sym, ch);
    }

    string? Override(SymbolQuery q)
    {
        foreach (var k in new[] { q.Isin, q.Ticker, q.Name })
            if (k.Length > 0 && _overrides.TryGetValue(k, out var s) && !string.IsNullOrWhiteSpace(s)) return s.Trim();
        return null;
    }

    async Task<ChartData?> TryChart(string symbol, CancellationToken ct)
    {
        try { return await _yahoo.ChartAsync(symbol, "1d", "5m", ct); }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Once("chart:" + symbol, $"Yahoo chart for {symbol} failed: {e.Message}"); return null; }
    }

    static Dictionary<string, string> LoadCache()
    {
        try
        {
            if (File.Exists(Paths.SymbolCache))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Paths.SymbolCache)) ?? new();
        }
        catch (Exception e)
        {
            Log.Warn($"symbols.json unreadable: {e.Message}");
        }
        return new();
    }

    public void SaveCache()
    {
        lock (_gate)
        {
            if (!_dirty) return;
            try
            {
                Directory.CreateDirectory(Paths.DataDir);
                File.WriteAllText(Paths.SymbolCache, JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true }));
                _dirty = false;
            }
            catch (Exception e)
            {
                Log.Warn($"symbols.json not saved: {e.Message}");
            }
        }
    }
}
