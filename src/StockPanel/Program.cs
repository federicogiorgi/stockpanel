namespace StockPanel;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled", e.ExceptionObject as Exception);

        var screenshot = ArgValue(args, "--screenshot");
        var windowed = args.Contains("--windowed");

        Mutex? instance = null;
        if (screenshot is null)
        {
            instance = AcquireSingleInstance();
            if (instance is null) return 0;
        }

        Log.Info("stockpanel start");
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.Error("UI thread", e.Exception);
        Application.Run(new PanelWindow(AppContext.BaseDirectory, windowed, screenshot));
        instance?.ReleaseMutex();
        return Environment.ExitCode;
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
