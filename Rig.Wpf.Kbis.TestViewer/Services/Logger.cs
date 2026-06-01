using System;
using System.IO;

namespace Rig.Wpf.Kbis.TestViewer.Services;

/// <summary>
/// Logger fichier minimaliste (sans Serilog, sans deps). Écrit chaque ligne dans
/// <c>%LocalAppData%\rig-wpf-testviewer\testviewer-yyyyMMdd.log</c>. Append, thread-safe.
/// </summary>
public static class Log
{
    private static readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "rig-wpf-testviewer");

    private static readonly object _lock = new();

    public static string FilePath { get; } = Path.Combine(_dir,
        "testviewer-" + DateTime.Now.ToString("yyyyMMdd") + ".log");

    static Log()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            Write("BOOT", $"───────── Session start {DateTime.Now:yyyy-MM-dd HH:mm:ss} ─────────");
        }
        catch { /* logger ne doit JAMAIS faire crasher l'app */ }
    }

    public static void Info(string msg)  => Write("INFO", msg);
    public static void Warn(string msg)  => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null)
        => Write("ERR ", ex is null ? msg : msg + "\n  " + ex.GetType().Name + " : " + ex.Message);

    private static void Write(string level, string msg)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(FilePath,
                    $"{DateTime.Now:HH:mm:ss.fff} {level} {msg}\n");
            }
        }
        catch { /* swallow */ }
    }
}
