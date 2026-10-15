using System.Net;
using System.Text.Json;

namespace StockPanel;

/// <summary>One Yahoo Finance chart: the latest quote plus a price series. Prices are in <see cref="Currency"/>
/// (main unit: pence are already converted to pounds). RegularStart..RegularEnd is the market's current or
/// next regular session (Yahoo's currentTradingPeriod), used to tell when the market is open. QuoteUnit is the
/// minor unit the market quotes in (GBp for London: pence), null when it quotes in the main unit.</summary>
public sealed record ChartData(
    string Symbol, string Currency, double Price, double? PrevClose, long MarketTime,
    long? PeriodStart, long? PeriodEnd, bool IsOpen, long[] T, double[] C,
    long? RegularStart = null, long? RegularEnd = null, string? QuoteUnit = null);

public sealed record SearchHit(string Symbol, string Exchange, string QuoteType);

public sealed class YahooClient : IDisposable
{
    static readonly string[] Hosts = ["query1.finance.yahoo.com", "query2.finance.yahoo.com"];
    readonly HttpClient _http;

    public YahooClient()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>Null when Yahoo has no data for the symbol; throws on network trouble.</summary>
    public async Task<ChartData?> ChartAsync(string symbol, string range, string interval, CancellationToken ct)
    {
        var path = $"/v8/finance/chart/{Uri.EscapeDataString(symbol)}?range={range}&interval={interval}&includePrePost=false";
        using var doc = await GetJsonAsync(path, ct);
        return doc is null ? null : ParseChart(doc.RootElement, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public async Task<List<SearchHit>> SearchAsync(string query, CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        using var doc = await GetJsonAsync($"/v1/finance/search?q={Uri.EscapeDataString(query)}&quotesCount=10&newsCount=0&listsCount=0", ct);
        if (doc is null || !doc.RootElement.TryGetProperty("quotes", out var quotes) || quotes.ValueKind != JsonValueKind.Array) return hits;
        foreach (var q in quotes.EnumerateArray())
        {
            var symbol = Str(q, "symbol");
            if (symbol is null) continue;
            hits.Add(new SearchHit(symbol, Str(q, "exchange") ?? "", Str(q, "quoteType") ?? ""));
        }
        return hits;
    }

    /// <summary>Tries both Yahoo hosts. Null for "not found" answers (404, or an error body).</summary>
    async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var host in Hosts)
        {
            try
            {
                using var resp = await _http.GetAsync($"https://{host}{path}", ct);
                if (resp.StatusCode == HttpStatusCode.NotFound) return null;
                if (!resp.IsSuccessStatusCode)
                {
                    last = new HttpRequestException($"HTTP {(int)resp.StatusCode} from {host}");
                    continue;
                }
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
            {
                last = e;
            }
        }
        throw last ?? new HttpRequestException("Yahoo Finance unreachable");
    }

    internal static ChartData? ParseChart(JsonElement root, long nowUnix)
    {
        if (!root.TryGetProperty("chart", out var chart) || !chart.TryGetProperty("result", out var results)
            || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0) return null;
        var r = results[0];
        if (!r.TryGetProperty("meta", out var meta)) return null;

        var rawCurrency = Str(meta, "currency") ?? "";
        var (currency, factor) = Lists.MainUnit(rawCurrency);
        var price = Num(meta, "regularMarketPrice");
        if (price is null) return null;
        var prev = Num(meta, "previousClose") ?? Num(meta, "chartPreviousClose");

        long? periodStart = null, periodEnd = null;
        if (meta.TryGetProperty("tradingPeriods", out var tps) && tps.ValueKind == JsonValueKind.Array)
        {
            // range=1d: [[{start,end}]] for the day the data belongs to
            foreach (var day in tps.EnumerateArray())
            {
                var p = day.ValueKind == JsonValueKind.Array && day.GetArrayLength() > 0 ? day[0] : day;
                if (p.ValueKind != JsonValueKind.Object) continue;
                periodStart = Long(p, "start");
                periodEnd = Long(p, "end");
            }
        }
        var isOpen = false;
        long? regStart = null, regEnd = null;
        if (meta.TryGetProperty("currentTradingPeriod", out var ctp) && ctp.TryGetProperty("regular", out var reg))
        {
            var s = Long(reg, "start"); var e = Long(reg, "end");
            regStart = s; regEnd = e;
            isOpen = s <= nowUnix && nowUnix < e;
            periodStart ??= s;
            periodEnd ??= e;
        }

        var times = new List<long>();
        var closes = new List<double>();
        if (r.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Array
            && r.TryGetProperty("indicators", out var ind) && ind.TryGetProperty("quote", out var qa)
            && qa.ValueKind == JsonValueKind.Array && qa.GetArrayLength() > 0
            && qa[0].TryGetProperty("close", out var cl) && cl.ValueKind == JsonValueKind.Array)
        {
            var n = Math.Min(ts.GetArrayLength(), cl.GetArrayLength());
            for (var i = 0; i < n; i++)
            {
                if (cl[i].ValueKind != JsonValueKind.Number || ts[i].ValueKind != JsonValueKind.Number) continue;
                var v = cl[i].GetDouble();
                if (!double.IsFinite(v) || v <= 0) continue;
                times.Add(ts[i].GetInt64());
                closes.Add(v * factor);
            }
        }

        return new ChartData(Str(meta, "symbol") ?? "", currency, price.Value * factor, prev * factor,
            Long(meta, "regularMarketTime") ?? nowUnix, periodStart, periodEnd, isOpen, times.ToArray(), closes.ToArray(),
            regStart, regEnd, factor != 1 ? rawCurrency : null);
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.GetDouble() is var d && double.IsFinite(d) ? d : null;

    static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : null;

    public void Dispose() => _http.Dispose();
}
