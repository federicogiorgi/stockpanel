using System.Text.Json;

namespace StockPanel;

public sealed class MonitorConfig
{
    public int Width { get; set; } = 400;
    public int Height { get; set; } = 1280;
    public string? DeviceName { get; set; }
}

public sealed class WeatherConfig
{
    public string Place { get; set; } = "Bologna";
    public double Latitude { get; set; } = 44.4949;
    public double Longitude { get; set; } = 11.3426;
}

public sealed class PanelConfig
{
    /// <summary>Seconds between quote updates (at least 15).</summary>
    public int RefreshSeconds { get; set; } = 60;
    /// <summary>Yahoo range of the long chart: 1mo, 3mo, 6mo, 1y, 2y, 5y, ytd or max.</summary>
    public string HistoryRange { get; set; } = "6mo";
    public int HistoryRefreshMinutes { get; set; } = 30;
    public string BaseCurrency { get; set; } = "EUR";
    public string StocksFile { get; set; } = "list_stocks.csv";
    public string OthersFile { get; set; } = "list_others.csv";
    /// <summary>Order of the stock tiles: file, name, daily, overall, value.</summary>
    public string SortBy { get; set; } = "file";
    public MonitorConfig Monitor { get; set; } = new();
    public WeatherConfig Weather { get; set; } = new();
    /// <summary>Yahoo Finance symbol to use for an ISIN, a ticker or a name, when the automatic choice is wrong.</summary>
    public Dictionary<string, string> Symbols { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class ConfigLoader
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static (PanelConfig Config, string? Warning) Load(string path)
    {
        if (!File.Exists(path)) return (new PanelConfig(), null);
        try
        {
            var cfg = JsonSerializer.Deserialize<PanelConfig>(File.ReadAllText(path), Options) ?? new PanelConfig();
            cfg.RefreshSeconds = Math.Clamp(cfg.RefreshSeconds, 15, 3600);
            cfg.HistoryRefreshMinutes = Math.Clamp(cfg.HistoryRefreshMinutes, 5, 1440);
            cfg.Symbols = new Dictionary<string, string>(cfg.Symbols ?? new(), StringComparer.OrdinalIgnoreCase);
            cfg.Monitor ??= new();
            cfg.Weather ??= new();
            if (string.IsNullOrWhiteSpace(cfg.BaseCurrency)) cfg.BaseCurrency = "EUR";
            cfg.BaseCurrency = cfg.BaseCurrency.Trim().ToUpperInvariant();
            return (cfg, null);
        }
        catch (Exception e)
        {
            return (new PanelConfig(), $"config.json is invalid ({e.Message}); using defaults");
        }
    }
}
