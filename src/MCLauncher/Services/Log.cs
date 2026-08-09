using System.Text;

namespace MCLauncher.Services;

public static class Log
{
    private static readonly object Sync = new();

    public static event Action<string>? LineWritten;

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Error(string msg, Exception ex) => Write("ERROR", msg + " :: " + ex);

    private static void Write(string level, string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {msg}";
        LineWritten?.Invoke(line);

        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LauncherPaths.Root);
                File.AppendAllText(LauncherPaths.LauncherLogFile, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* логирование не должно ронять приложение */ }
    }
}
