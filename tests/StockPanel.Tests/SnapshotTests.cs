using System.Text.Json;

namespace StockPanel.Tests;

public class SnapshotTests
{
    const string Chart = """
        {"chart":{"result":[{"meta":{"currency":"GBp","symbol":"SHEL.L","regularMarketPrice":2600,"previousClose":2500,
         "regularMarketTime":1000,"tradingPeriods":[[{"start":0,"end":3000}]],
         "currentTradingPeriod":{"regular":{"start":0,"end":3000}}},
         "timestamp":[0,300,600],"indicators":{"quote":[{"close":[2500,null,2550]}]}}]}}
        """;

    [Fact]
    public void ParseChart_ConvertsPenceAndSkipsGaps()
    {
        using var doc = JsonDocument.Parse(Chart);
        var c = YahooClient.ParseChart(doc.RootElement, 1500)!;
        Assert.Equal("GBP", c.Currency);
        Assert.Equal(26, c.Price, 6);
        Assert.Equal(25, c.PrevClose!.Value, 6);
        Assert.Equal([0L, 600L], c.T);
        Assert.True(c.IsOpen);
    }

    static ChartData Day(double prev, double price, long[] t, double[] c, long start, long end) =>
        new("X", "EUR", price, prev, t[^1], start, end, false, t, c);

    [Fact]
    public void Build_ComputesDailyOverallAndProfitInBaseCurrency()
    {
        var cfg = new PanelConfig();
        var h = new Holding("ACME", "US0000000001", "ACME.O", "NASDAQ", "Industrials", "USD", 100, 10, null);
        var s = new Instrument { Holding = h, Symbol = "ACME", Day = Day(110, 121, [0, 300], [115, 121], 0, 600) with { Currency = "USD" }, DayFetchedUtc = DateTime.UtcNow };
        var fx = new Dictionary<string, ChartData> { ["USD"] = Day(1.1, 1.1, [0], [1.1], 0, 600) };
        var snap = SnapshotBuilder.Build(cfg, [s], [], fx, null, null, [], DateTime.UtcNow, DateTimeOffset.UtcNow);
        var st = snap.Stocks[0];
        Assert.Equal(10, st.ChangePct);           // 110 -> 121
        Assert.Equal(21, st.OverallPct);          // bought at 100 USD
        Assert.Equal(190.91, st.Pl!.Value, 2);    // 21 USD x 10 / 1.1
        Assert.Equal(100, st.Day!.Value, 2);      // 11 USD x 10 / 1.1
        Assert.Equal(1100, st.Value!.Value, 2);
        var usd = Assert.Single(snap.Extras);
        Assert.Equal("USD/EUR", usd.Name);
        Assert.Equal(0.9091, usd.Price, 4);
    }

    [Fact]
    public void Build_PurchasePriceInEuro_ForADollarStock()
    {
        var cfg = new PanelConfig();
        // bought for 100 EUR a share; now 121 USD at 1.10 USD/EUR = 110 EUR
        var h = new Holding("ACME", "US0000000001", "ACME.O", "NASDAQ", "Industrials", "USD", 100, 10, null, "EUR");
        var s = new Instrument { Holding = h, Symbol = "ACME", Day = Day(110, 121, [0, 300], [115, 121], 0, 600) with { Currency = "USD" }, DayFetchedUtc = DateTime.UtcNow };
        var fx = new Dictionary<string, ChartData> { ["USD"] = Day(1.1, 1.1, [0], [1.1], 0, 600) };
        var st = SnapshotBuilder.Build(cfg, [s], [], fx, null, null, [], DateTime.UtcNow, DateTimeOffset.UtcNow).Stocks[0];
        Assert.Equal(10, st.OverallPct!.Value, 2);
        Assert.Equal(100, st.Pl!.Value, 2);
    }
}
