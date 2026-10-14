using System.Diagnostics;
using System.Drawing;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace StockPanel;

public sealed class PanelWindow : Form
{
    const string Host = "stockpanel.local";
    static readonly Color Background = Color.FromArgb(0x10, 0x11, 0x13);

    readonly string _appDir;
    readonly string _dataDir;
    readonly bool _forceWindowed;
    readonly string? _screenshotPath;
    readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };
    readonly System.Windows.Forms.Timer _monitorTimer = new() { Interval = 10_000 };
    readonly System.Windows.Forms.Timer _reloadTimer = new() { Interval = 1500 };
    readonly ContextMenuStrip _menu = new();
    readonly FileSystemWatcher? _watcher;
    PanelConfig _cfg = new();
    string? _configWarning;
    DataService? _service;
    string? _lastJson;
    bool _pageReady;

    public PanelWindow(string appDir, bool forceWindowed, string? screenshotPath)
    {
        _appDir = appDir;
        _dataDir = FindDataDir(appDir);
        _forceWindowed = forceWindowed;
        _screenshotPath = screenshotPath;

        Text = "stockpanel";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath);
        BackColor = Background;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        Controls.Add(_web);

        _menu.Items.Add("Refresh now", null, (_, _) => Reload());
        _menu.Items.Add("Edit stocks list", null, (_, _) => Open(Path.Combine(_dataDir, _cfg.StocksFile)));
        _menu.Items.Add("Edit indices list", null, (_, _) => Open(Path.Combine(_dataDir, _cfg.OthersFile)));
        _menu.Items.Add("Open folder", null, (_, _) => Open(_dataDir));
        _menu.Items.Add("Save screenshot…", null, async (_, _) => await SaveScreenshotDialogAsync());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => Close());

        LoadConfig();
        Place(); // decide full screen vs window before the first paint
        Load += async (_, _) => await InitAsync();
        DpiChanged += (_, e) => { e.Cancel = true; Place(); };
        _monitorTimer.Tick += (_, _) => Place();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // Editing a list or config.json reloads the panel by itself (after the editor has finished saving).
        _reloadTimer.Tick += (_, _) => { _reloadTimer.Stop(); Log.Info("Lists or config changed: reloading"); Reload(); };
        if (screenshotPath is null)
        {
            try
            {
                _watcher = new FileSystemWatcher(_dataDir) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                _watcher.Changed += OnFileChanged;
                _watcher.Created += OnFileChanged;
                _watcher.Renamed += OnFileChanged;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception e)
            {
                Log.Warn($"Not watching {_dataDir}: {e.Message}");
            }
        }
    }

    /// <summary>config.json and the lists live next to the exe; when running from a build folder, in the first
    /// parent folder that has them.</summary>
    static string FindDataDir(string appDir)
    {
        for (var dir = new DirectoryInfo(appDir); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "list_stocks.csv")) || File.Exists(Path.Combine(dir.FullName, "config.json")))
                return dir.FullName;
        return appDir;
    }

    void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        var name = Path.GetFileName(e.FullPath);
        if (!string.Equals(name, _cfg.StocksFile, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(name, _cfg.OthersFile, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(name, "config.json", StringComparison.OrdinalIgnoreCase)) return;
        try { BeginInvoke(() => { _reloadTimer.Stop(); _reloadTimer.Start(); }); }
        catch (InvalidOperationException) { }
    }

    void OnDisplaySettingsChanged(object? sender, EventArgs e) => BeginInvoke(Place);

    /// <summary>Mini-monitor present: borderless full screen on it. Otherwise (or with --windowed): a normal
    /// window centred on the main monitor. Re-checked every 10 s and on display changes.</summary>
    void Place()
    {
        if (IsDisposed) return;
        var screens = Screen.AllScreens.Select(s => new ScreenInfo(s.DeviceName, s.Bounds)).ToList();
        var target = _forceWindowed || _screenshotPath is not null ? null : MonitorPicker.Pick(screens, _cfg.Monitor);
        if (target is not null)
        {
            if (_mode != Mode.FullScreen)
            {
                _mode = Mode.FullScreen;
                FormBorderStyle = FormBorderStyle.None;
                TopMost = true;
                Log.Info($"Full screen on {target.DeviceName}");
            }
            if (Bounds != target.Bounds) Bounds = target.Bounds;
        }
        else if (_mode != Mode.Windowed)
        {
            _mode = Mode.Windowed;
            TopMost = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
            ClientSize = _screenshotPath is not null ? new Size(400, 1280)
                : WindowPlacement.WindowedClientSize(screen.WorkingArea.Size, ScaleOf(screen));
            Location = WindowPlacement.Centre(screen.WorkingArea, Size);
            Log.Info(_forceWindowed ? "Windowed (--windowed)" : "Mini-monitor not found: windowed on the main monitor");
        }
    }

    enum Mode { None, FullScreen, Windowed }
    Mode _mode = Mode.None;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(Point pt, uint flags);

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>Windows display scaling of a monitor (1.5 for 150 %).</summary>
    static double ScaleOf(Screen screen)
    {
        var monitor = MonitorFromPoint(new Point(screen.Bounds.X + 1, screen.Bounds.Y + 1), 2 /* nearest */);
        return GetDpiForMonitor(monitor, 0 /* effective */, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
    }

    async Task InitAsync()
    {
        try
        {
            _monitorTimer.Start();
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.DataDir, "webview2"));
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = _forceWindowed;
            core.SetVirtualHostNameToFolderMapping(Host, Path.Combine(_appDir, "web"), CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWebMessage;
            core.NavigationStarting += (_, _) => _pageReady = false;
            core.NavigationCompleted += (_, e) =>
            {
                _pageReady = e.IsSuccess;
                if (_pageReady && _lastJson is not null) Post(_lastJson);
            };
            core.ProcessFailed += OnProcessFailed;
            core.Navigate($"https://{Host}/index.html");

            // Show the last known numbers right away (marked as old) while the first fetch runs.
            if (_screenshotPath is null && File.Exists(Paths.LastSnapshot))
            {
                try { _lastJson = MarkCached(await File.ReadAllTextAsync(Paths.LastSnapshot)); }
                catch (Exception e) { Log.Warn($"last.json unreadable: {e.Message}"); }
            }
            if (_screenshotPath is not null)
            {
                // give up waiting for live data after a while and capture whatever is there
                var t = new System.Windows.Forms.Timer { Interval = 90_000 };
                t.Tick += async (_, _) => { t.Stop(); await ScreenshotAndClose(); };
                t.Start();
            }
            StartService();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Fail("The Microsoft Edge WebView2 Runtime is missing.\n\nInstall it from https://developer.microsoft.com/microsoft-edge/webview2/", null);
        }
        catch (Exception e)
        {
            Fail(@"stockpanel could not start; see %LOCALAPPDATA%\stockpanel\stockpanel.log.", e);
        }
    }

    static string MarkCached(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json);
        if (node is System.Text.Json.Nodes.JsonObject o) o["cached"] = true;
        return node?.ToJsonString() ?? json;
    }

    void Fail(string message, Exception? e)
    {
        Log.Error("Start-up failed: " + message, e);
        Environment.ExitCode = 1;
        MessageBox.Show(message, "stockpanel", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Close();
    }

    void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Warn($"WebView2 process failed: {e.ProcessFailedKind} ({e.Reason})");
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            Environment.ExitCode = 2;
            BeginInvoke(Close);
        }
        else
        {
            BeginInvoke(() => _web.CoreWebView2?.Reload());
        }
    }

    void LoadConfig()
    {
        var (cfg, warning) = ConfigLoader.Load(Path.Combine(_dataDir, "config.json"));
        if (warning is not null) Log.Warn(warning);
        _cfg = cfg;
        _configWarning = warning;
    }

    void StartService()
    {
        _service?.Dispose();
        _service = new DataService(_cfg, _dataDir, _configWarning is null ? [] : [_configWarning], Publish);
    }

    void Publish(string json)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                _lastJson = json;
                Post(json);
            });
        }
        catch (InvalidOperationException)
        {
            // Window handle gone during shutdown.
        }
    }

    void Post(string json)
    {
        if (!_pageReady || IsDisposed) return;
        try
        {
            _web.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception e)
        {
            Log.Once("publish:" + e.GetType().Name, $"Posting to the page failed: {e.Message}");
        }
    }

    async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string message;
        try { message = e.TryGetWebMessageAsString(); }
        catch (ArgumentException) { return; }

        if (message == "contextmenu") _menu.Show(Cursor.Position);
        else if (message == "rendered" && _screenshotPath is not null) await ScreenshotAndClose();
    }

    bool _capturing;
    async Task ScreenshotAndClose()
    {
        if (_capturing || _screenshotPath is null) return;
        _capturing = true;
        try
        {
            await Task.Delay(800);
            await SaveScreenshotAsync(_screenshotPath);
        }
        catch (Exception ex)
        {
            Log.Error("Screenshot failed", ex);
            Environment.ExitCode = 1;
        }
        Close();
    }

    void Reload()
    {
        LoadConfig();
        StartService();
        Place();
        _web.CoreWebView2?.Reload(); // also picks up edits to the page; the last numbers are re-posted on load
    }

    static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) { Log.Warn($"Cannot open {path}: {e.Message}"); }
    }

    async Task SaveScreenshotAsync(string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var file = File.Create(full);
        await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
        Log.Info($"Screenshot saved to {full}");
    }

    async Task SaveScreenshotDialogAsync()
    {
        using var dialog = new SaveFileDialog { Filter = "PNG image|*.png", FileName = $"stockpanel-{DateTime.Now:yyyyMMdd-HHmmss}.png" };
        if (dialog.ShowDialog(this) == DialogResult.OK) await SaveScreenshotAsync(dialog.FileName);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _monitorTimer.Stop();
        _reloadTimer.Stop();
        _watcher?.Dispose();
        _service?.Dispose();
        base.OnFormClosing(e);
    }
}
