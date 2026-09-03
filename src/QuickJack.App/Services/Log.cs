using System.IO;
using QuickJack.Core.Storage;

namespace QuickJack.App.Services;

/// <summary>
/// A minimal rolling log. A tray app has no console and no window to print to, so without
/// this a startup failure is completely silent — which is exactly when you need detail.
/// </summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private static string? _path;

    public static void Initialise(QuickJackPaths paths)
    {
        _path = Path.Combine(paths.UserDirectory, "app.log");

        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length > 512 * 1024)
                File.Delete(_path);
        }
        catch (IOException) { }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        if (_path is null) return;

        lock (Gate)
        {
            try
            {
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never be the thing that breaks the app.
            }
        }
    }
}
