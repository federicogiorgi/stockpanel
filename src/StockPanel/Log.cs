namespace StockPanel;

public static class Paths
{
    public static string DataDir { get; internal set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "stockpanel");

    public static string SymbolCache => Path.Combine(DataDir, "symbols.json");
    public static string LastSnapshot => Path.Combine(DataDir, "last.json");
}

public static class Log
{
    static readonly object Gate = new();
    static readonly HashSet<string> Seen = new();

    public static string FilePath => Path.Combine(Paths.DataDir, "stockpanel.log");

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message, Exception? e = null) =>
        Write("ERROR", e is null ? message : $"{message}: {e}");

    /// <summary>Logs a warning the first time a key is seen; later calls are ignored.</summary>
    public static void Once(string key, string message)
    {
        lock (Gate) { if (!Seen.Add(key)) return; }
        Warn(message);
    }

    static void Write(string level, string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Paths.DataDir);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000)
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never take the panel down.
            }
        }
    }
}
