using Microsoft.Win32;

namespace StockPanel.Tests;

public class StartupTests : IDisposable
{
    readonly string _root = $@"Software\stockpanel-tests\{Guid.NewGuid():N}";
    readonly StartupRegistration _reg;

    public StartupTests() => _reg = new StartupRegistration(_root + @"\Run", _root + @"\Approved");

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);

    [Fact]
    public void NewEntry_IsAddedSwitchedOff()
    {
        Assert.True(_reg.EnsureRunEntry(@"D:\x\StockPanel.exe"));
        Assert.Equal(@"""D:\x\StockPanel.exe""", _reg.RunCommand());
        Assert.False(_reg.IsEnabled());
    }

    [Fact]
    public void ExistingEntry_FollowsTheExeAndKeepsTheSwitch()
    {
        _reg.EnsureRunEntry(@"D:\old\StockPanel.exe");
        using (var k = Registry.CurrentUser.CreateSubKey(_root + @"\Approved"))
            k.SetValue("stockpanel", new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        Assert.False(_reg.EnsureRunEntry(@"D:\new\StockPanel.exe"));
        Assert.Equal(@"""D:\new\StockPanel.exe""", _reg.RunCommand());
        Assert.True(_reg.IsEnabled());
    }

    [Fact]
    public void Remove_DeletesBoth()
    {
        _reg.EnsureRunEntry(@"D:\x\StockPanel.exe");
        _reg.Remove();
        Assert.Null(_reg.RunCommand());
        Assert.Null(_reg.IsEnabled());
    }
}
