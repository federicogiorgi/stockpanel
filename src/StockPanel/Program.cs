namespace StockPanel;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled", e.ExceptionObject as Exception);

        if (args.Contains("--remove-startup")) return RemoveStartup();

        var screenshot = ArgValue(args, "--screenshot");
        var windowed = args.Contains("--windowed");

        Mutex? instance = null;
        if (screenshot is null)
        {
            instance = AcquireSingleInstance();
            if (instance is null) return 0;
        }

        Log.Info("stockpanel start");
        if (screenshot is null) EnsureStartupEntry();
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.Error("UI thread", e.Exception);
        Application.Run(new PanelWindow(AppContext.BaseDirectory, windowed, screenshot));
        instance?.ReleaseMutex();
        return Environment.ExitCode;
    }

    /// <summary>Keeps the Settings › Apps › Startup entry pointing at this exe (added switched off the first time).</summary>
    static void EnsureStartupEntry()
    {
        try
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            if (new StartupRegistration().EnsureRunEntry(exe)) Log.Info("Added stockpanel to Settings > Apps > Startup (switched off)");
        }
        catch (Exception e)
        {
            Log.Warn($"Startup entry not updated: {e.Message}");
        }
    }

    static int RemoveStartup()
    {
        new StartupRegistration().Remove();
        Log.Info("Removed the Startup entry");
        MessageBox.Show("stockpanel was removed from Startup apps.\n\nStarting StockPanel.exe adds it back (switched off).",
            "stockpanel", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static Mutex? AcquireSingleInstance()
    {
        var m = new Mutex(true, @"Local\stockpanel", out var created);
        if (created) return m;
        m.Dispose();
        Log.Info("Another stockpanel is already running; exiting");
        return null;
    }
}
