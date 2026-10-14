namespace StockPanel.Tests;

public class ListsTests
{
    static string Temp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"stockpanel-test-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void BrokerExport_DerivesQuantityFromValueAndPrice()
    {
        var path = Temp("""
            Stock Name,ISIN,Ticker,Purchase Price,Current Price,Currency,Current Total Value (Original Currency),Current Total Value (EUR),Market,Sector
            ACME,US0000000001,ACME.O,150.00,200.00,USD,3000.00,2690.00,NASDAQ,Information Technology
            "WIDGETS, INC",US0000000002,WDG.N,40.00,50.00,USD,1000.00,897.00,NYSE,Financials
            """);
        var (items, warnings) = Lists.LoadStocks(path);
        Assert.Empty(warnings);
        Assert.Equal(2, items.Count);
        Assert.Equal(15, items[0].Quantity);
        Assert.Equal(150.00, items[0].BuyPrice);
        Assert.Equal("USD", items[0].Currency);
        Assert.Equal("WIDGETS, INC", items[1].Name);
        Assert.Equal(20, items[1].Quantity);
    }

    [Fact]
    public void ItalianExcel_SemicolonsAndDecimalCommas()
    {
        var path = Temp("Stock Name;Ticker;Purchase Price;Quantity;Currency;Sector\r\nSIEMENS;SIE.EQ;1.445,20;15;EUR;Industrials\r\n");
        var (items, _) = Lists.LoadStocks(path);
        Assert.Single(items);
        Assert.Equal(1445.20, items[0].BuyPrice, 6);
        Assert.Equal(15, items[0].Quantity);
    }

    [Fact]
    public void Pence_AreConvertedToPounds()
    {
        var path = Temp("Name,Ticker,Purchase Price,Quantity,Currency\nSHELL,SHEL.L,2500,10,GBX\n");
        var (items, _) = Lists.LoadStocks(path);
        Assert.Equal("GBP", items[0].Currency);
        Assert.Equal(25, items[0].BuyPrice, 6);
    }

    [Fact]
    public void Others_UseShortName()
    {
        var path = Temp("Name,Short name,ISIN\niShares Core S&P 500 UCITS ETF USD (Acc),iShares S&P 500,IE00B5BMR087\n");
        var (items, _) = Lists.LoadOthers(path);
        Assert.Equal("iShares S&P 500", items[0].ShortName);
        Assert.Equal("IE00B5BMR087", items[0].Isin);
    }

    [Theory]
    [InlineData(14.999, 15)]
    [InlineData(2.5, 2.5)]
    [InlineData(0.12345678, 0.1235)]
    public void RoundQuantity(double raw, double expected) => Assert.Equal(expected, Lists.RoundQuantity(raw));
}
