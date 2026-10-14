namespace StockPanel.Tests;

public class SymbolRulesTests
{
    [Theory]
    [InlineData("AAPL.O", "US0378331005", "NASDAQ", "AAPL")]
    [InlineData("KO.N", "US1912161007", "NYSE", "KO")]
    [InlineData("MC.EQ", "FR0000121014", "EQUIDUCT", "MC.PA")]
    [InlineData("SAP.EQ", "DE0007164600", "EQUIDUCT", "SAP.DE")]
    [InlineData("SIE.FRA", "DE0007236101", "XETRA", "SIE.DE")]
    [InlineData("ENI.MI", "IT0003132476", "", "ENI.MI")]
    [InlineData("BRKb.N", "US0846707026", "", "BRK-B")]
    [InlineData("XYZ.EQ", "LU0000000000", "EQUIDUCT", null)]
    public void FromTicker(string ticker, string isin, string market, string? expected) =>
        Assert.Equal(expected, SymbolRules.FromTicker(ticker, isin, market));

    [Fact]
    public void Rank_PrefersEuroListings()
    {
        var hits = new[] { new SearchHit("VALL.L", "LSE", "ETF"), new SearchHit("X.SG", "STU", "ETF"), new SearchHit("WEBN.DE", "GER", "ETF") };
        Assert.Equal(["WEBN.DE", "VALL.L", "X.SG"], SymbolRules.Rank(hits).Select(h => h.Symbol));
    }
}
