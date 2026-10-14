namespace StockPanel;

/// <summary>Background loop: reads the two lists, finds the Yahoo symbols, then every RefreshSeconds fetches
/// the day's quotes (and the weather less often) and publishes a snapshot as JSON.</summary>
public sealed class DataService : IDisposable
{
    static readonly TimeSpan WeatherEvery = TimeSpan.FromMinutes(10);
    static readonly TimeSpan ResolveRetry = TimeSpan.FromMinutes(10);

    readonly PanelConfig _cfg;
    readonly string _dataDir;
    readonly IReadOnlyList<string> _extraWarnings;
    readonly Action<string> _publish;
    readonly CancellationTokenSource _cts = new();
    readonly YahooClient _yahoo = new();
    readonly SemaphoreSlim _gate = new(6);

    public Task Completion { get; }

    public DataService(PanelConfig cfg, string dataDir, IReadOnlyList<string> extraWarnings, Action<string> publish)
    {
        _cfg = cfg;
        _dataDir = dataDir;
        _extraWarnings = extraWarnings;
        _publish = publish;
        Completion = Task.Run(() => RunAsync(_cts.Token));
    }

    async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await LoopAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            Log.Error("Data loop stopped", e);
        }
    }

    async Task LoopAsync(CancellationToken ct)
    {
        var fallback = new List<string>();
        var (holdings, w1) = Lists.LoadStocks(ListPath(_cfg.StocksFile, fallback));
        var (others, w2) = Lists.LoadOthers(ListPath(_cfg.OthersFile, fallback));
        var listWarnings = _extraWarnings.Concat(fallback).Concat(w1).Concat(w2).ToList();
        foreach (var w in listWarnings) Log.Warn(w);
        Log.Info($"{holdings.Count} stocks, {others.Count} indices");

        var stocks = holdings.Select(h => new Instrument { Holding = h }).ToList();
        var indices = others.Select(o => new Instrument { Index = o }).ToList();
        var all = indices.Concat(stocks).ToList();
        var resolver = new SymbolResolver(_yahoo, _cfg.Symbols);
        var fx = new Dictionary<string, ChartData>(StringComparer.OrdinalIgnoreCase);
        var fxCurrencies = holdings.SelectMany(h => new[] { h.Currency, h.BuyCurrency }).Append("USD")
            .Where(c => !string.Equals(c, _cfg.BaseCurrency, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        WeatherNow? weather = null;
        ChartData? btc = null;
        var nextWeather = DateTime.MinValue;
        DateTime? lastDataUtc = null;

        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            var warnings = new List<string>(listWarnings);

            var weatherTask = started >= nextWeather ? FetchWeather(ct) : Task.FromResult<WeatherNow?>(null);

            // symbols (first round, and retries for the ones not found)
            await Task.WhenAll(all.Where(i => i.Symbol is null && started >= i.NextResolveUtc).Select(i => Limited(async () =>
            {
                var (symbol, chart) = await resolver.ResolveAsync(Query(i), ct);
                i.Symbol = symbol;
                if (chart is not null) { i.Day = chart; i.DayFetchedUtc = DateTime.UtcNow; }
                if (symbol is null) i.NextResolveUtc = DateTime.UtcNow + ResolveRetry;
            }, ct)));
            resolver.SaveCache();

            // day quotes and exchange rates
            var ok = 0;
            var tasks = all.Where(i => i.Symbol is not null && DateTime.UtcNow - i.DayFetchedUtc > TimeSpan.FromSeconds(10))
                .Select(i => Limited(async () =>
                {
                    var d = await Chart(i.Symbol!, "1d", "5m", ct);
                    if (d is null) return;
                    i.Day = d; i.DayFetchedUtc = DateTime.UtcNow; Interlocked.Increment(ref ok);
                }, ct))
                .Append(Limited(async () =>
                {
                    var d = await Chart("BTC-USD", "1d", "5m", ct);
                    if (d is not null) btc = d;
                }, ct))
                .Concat(fxCurrencies.Select(c => Limited(async () =>
                {
                    var d = await Chart($"{_cfg.BaseCurrency}{c}=X", "1d", "5m", ct);
                    if (d is null) return;
                    lock (fx) fx[c] = d;
                    Interlocked.Increment(ref ok);
                }, ct)));
            await Task.WhenAll(tasks);
            if (ok > 0 || all.Any(i => i.DayFetchedUtc >= started)) lastDataUtc = DateTime.UtcNow;

            var wx = await weatherTask;
            if (wx is not null) { weather = wx; nextWeather = started + WeatherEvery; }
            else if (started >= nextWeather) nextWeather = started.AddMinutes(1);

            foreach (var i in all.Where(i => i.Symbol is null))
                warnings.Add($"No Yahoo symbol for {i.Holding?.Name ?? i.Index!.ShortName}");
            if (lastDataUtc is null || DateTime.UtcNow - lastDataUtc > TimeSpan.FromSeconds(_cfg.RefreshSeconds * 3 + 30))
                warnings.Add("Yahoo Finance unreachable");

            Dictionary<string, ChartData> fxCopy;
            lock (fx) fxCopy = new(fx, StringComparer.OrdinalIgnoreCase);
            var json = SnapshotBuilder.Build(_cfg, stocks, indices, fxCopy, btc, weather, warnings, lastDataUtc, DateTimeOffset.Now).ToJson();
            _publish(json);
            SaveLast(json);

            var wait = TimeSpan.FromSeconds(_cfg.RefreshSeconds) - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        }
    }

    /// <summary>The list, or its ".example.csv" sibling (shipped in the repository) while there is none yet.</summary>
    string ListPath(string file, List<string> warnings)
    {
        var path = Path.Combine(_dataDir, file);
        var example = Path.Combine(_dataDir, Path.GetFileNameWithoutExtension(file) + ".example" + Path.GetExtension(file));
        if (File.Exists(path) || !File.Exists(example)) return path;
        warnings.Add($"Showing {Path.GetFileName(example)}: copy it to {file} and put in your own stocks");
        return example;
    }

    SymbolQuery Query(Instrument i) => i.Holding is { } h
        ? new SymbolQuery("S|" + (h.Isin.Length > 0 ? h.Isin : h.Ticker + "|" + h.Name), h.Name, h.Isin, h.Ticker, h.Market, h.Yahoo, h.Currency)
        : new SymbolQuery("I|" + (i.Index!.Isin.Length > 0 ? i.Index.Isin : i.Index.Ticker + "|" + i.Index.ShortName),
            i.Index.ShortName, i.Index.Isin, i.Index.Ticker, "", i.Index.Yahoo, _cfg.BaseCurrency);

    async Task<ChartData?> Chart(string symbol, string range, string interval, CancellationToken ct)
    {
        try
        {
            var d = await _yahoo.ChartAsync(symbol, range, interval, ct);
            if (d is null) Log.Once("nodata:" + symbol + range, $"Yahoo has no {range} data for {symbol}");
            return d;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Once($"fail:{symbol}:{range}:{DateTime.Now:yyyyMMddHH}", $"{symbol} {range}: {e.Message}");
            return null;
        }
    }

    async Task<WeatherNow?> FetchWeather(CancellationToken ct)
    {
        try
        {
            return await WeatherClient.FetchAsync(_cfg.Weather, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Once($"weather:{DateTime.Now:yyyyMMddHH}", $"Weather: {e.Message}");
            return null;
        }
    }

    async Task Limited(Func<Task> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await work(); }
        finally { _gate.Release(); }
    }

    static void SaveLast(string json)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            var tmp = Paths.LastSnapshot + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, Paths.LastSnapshot, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Once("last:" + e.GetType().Name, $"last.json not saved: {e.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        // the loop owns the HTTP client; let it finish before disposing
        Completion.ContinueWith(_ => { _yahoo.Dispose(); _cts.Dispose(); }, TaskScheduler.Default);
    }
}
