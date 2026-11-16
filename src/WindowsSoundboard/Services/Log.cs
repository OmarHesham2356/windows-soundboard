using System.Text;

namespace WindowsSoundboard.Services;

/// <summary>
/// Tiny append-only log. The app is a GUI with no console, so this is the only way
/// to see what happened on a machine that is not being debugged.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string LogPath = System.IO.Path.Combine(AppPaths.DataFolder, "app.log");

    public static string FilePath => LogPath;

    public static void Info(string message) => Write("info", message);

    public static void Warn(string message) => Write("warn", message);

    public static void Error(string message, Exception? exception = null)
        => Write("error", exception is null ? message : $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(AppPaths.DataFolder);
                var line = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" [").Append(level).Append("] ")
                    .Append(message)
                    .AppendLine()
                    .ToString();
                System.IO.File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
