using Microsoft.Win32;

namespace StockPanel;

/// <summary>The "stockpanel" entry in Settings › Apps › Startup: a value in the user's Run key, with the on/off
/// switch Windows keeps under Explorer\StartupApproved\Run (first byte 2 = on, 3 = off). Same scheme as paxpanel.
/// stockpanel needs no administrator rights, so the Run entry starts it directly.</summary>
public sealed class StartupRegistration(
    string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run",
    string approvedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run")
{
    const string ValueName = "stockpanel";

    public string? RunCommand()
    {
        using var k = Registry.CurrentUser.OpenSubKey(runKey);
        return k?.GetValue(ValueName) as string;
    }

    /// <summary>null: Windows has no switch recorded (counts as on); otherwise the switch position.</summary>
    public bool? IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(approvedKey);
        return k?.GetValue(ValueName) is byte[] { Length: > 0 } b ? b[0] % 2 == 0 : null;
    }

    /// <summary>Creates the entry switched off (true), or just updates its path, keeping the user's switch (false).</summary>
    public bool EnsureRunEntry(string exePath)
    {
        var command = $"\"{exePath}\"";
        var existed = RunCommand() is not null;
        using (var run = Registry.CurrentUser.CreateSubKey(runKey))
            if (!string.Equals(run.GetValue(ValueName) as string, command, StringComparison.OrdinalIgnoreCase))
                run.SetValue(ValueName, command, RegistryValueKind.String);
        if (existed) return false;
        using var approved = Registry.CurrentUser.CreateSubKey(approvedKey);
        var off = new byte[12];
        off[0] = 3;
        BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(off, 4);
        approved.SetValue(ValueName, off, RegistryValueKind.Binary);
        return true;
    }

    public void Remove()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(runKey, writable: true)) run?.DeleteValue(ValueName, throwOnMissingValue: false);
        using var approved = Registry.CurrentUser.OpenSubKey(approvedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
