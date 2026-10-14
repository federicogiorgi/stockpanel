using System.Globalization;
using System.Text.Json;

namespace StockPanel;

/// <summary>Current weather from Open-Meteo (free, no key). Code is the WMO weather code.</summary>
public sealed record WeatherNow(string Place, double Temp, int Code, bool IsDay, double? Min, double? Max);

public static class WeatherClient
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<WeatherNow> FetchAsync(WeatherConfig cfg, CancellationToken ct)
    {
        var lat = cfg.Latitude.ToString(CultureInfo.InvariantCulture);
        var lon = cfg.Longitude.ToString(CultureInfo.InvariantCulture);
        var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
                  "&current=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1";
        await using var stream = await Http.GetStreamAsync(url, ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var cur = doc.RootElement.GetProperty("current");
        double? First(string name) =>
            doc.RootElement.TryGetProperty("daily", out var d) && d.TryGetProperty(name, out var a)
            && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0 && a[0].ValueKind == JsonValueKind.Number ? a[0].GetDouble() : null;
        return new WeatherNow(cfg.Place, cur.GetProperty("temperature_2m").GetDouble(), cur.GetProperty("weather_code").GetInt32(),
            cur.GetProperty("is_day").GetInt32() == 1, First("temperature_2m_min"), First("temperature_2m_max"));
    }
}
