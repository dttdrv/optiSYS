using System.Text;

namespace OptiSYS.Services;

internal static class StartupLog
{
    private static readonly object Gate = new();
    private static readonly string LogDirectory =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "optiSYS",
            "logs");

    private static readonly string LogPath = Path.Combine(LogDirectory, "startup.log");

    public static string PathOnDisk => LogPath;

    public static void Write(string message)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(LogDirectory);
            using var stream = new FileStream(
                LogPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
        }
    }

    public static void WriteException(string context, Exception exception) =>
        Write($"{context}: {exception.GetType().FullName}: {exception}");
}
