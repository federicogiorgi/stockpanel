using System.Drawing;

namespace StockPanel;

public sealed record ScreenInfo(string DeviceName, Rectangle Bounds);

public static class MonitorPicker
{
    public static ScreenInfo? Pick(IReadOnlyList<ScreenInfo> screens, MonitorConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.DeviceName))
            return screens.FirstOrDefault(s => string.Equals(s.DeviceName, cfg.DeviceName, StringComparison.OrdinalIgnoreCase));
        return screens.FirstOrDefault(s => s.Bounds.Width == cfg.Width && s.Bounds.Height == cfg.Height);
    }
}

/// <summary>Size and position of the panel when it runs as a normal window (no mini-monitor, or --windowed).</summary>
public static class WindowPlacement
{
    const int ChromeAllowance = 80; // title bar and borders

    /// <summary>400 x 1280 scaled by the monitor's DPI factor, shrunk (same shape) if the screen is too short.</summary>
    public static Size WindowedClientSize(Size workingArea, double dpiScale)
    {
        var height = 1280 * dpiScale;
        var maxHeight = Math.Max(320, workingArea.Height - ChromeAllowance);
        if (height > maxHeight) height = maxHeight;
        return new Size((int)Math.Round(height * 400 / 1280), (int)Math.Round(height));
    }

    public static Point Centre(Rectangle workingArea, Size window) =>
        new(workingArea.X + Math.Max(0, (workingArea.Width - window.Width) / 2),
            workingArea.Y + Math.Max(0, (workingArea.Height - window.Height) / 2));
}
