namespace SoulsProx;

/// <summary>Tiny append-only log in the settings folder, so problems on a friend's PC can be diagnosed.</summary>
public static class Log
{
    private static readonly object Lock = new();
    private static string? _path;

    public static string? FilePath => _path;

    public static void Init(string directory, string fileName = "log.txt")
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, fileName);
        try
        {
            var info = new FileInfo(_path);
            if (info.Exists && info.Length > 1_000_000) File.Move(_path, _path + ".old", overwrite: true);
        }
        catch (IOException) { }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        if (_path is null) return;
        lock (Lock)
        {
            try { File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}"); }
            catch (IOException) { }
        }
    }
}
