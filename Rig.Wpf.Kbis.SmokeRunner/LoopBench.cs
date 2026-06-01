using System;
using System.IO;
using System.Linq;

namespace Rig.Wpf.Kbis.SmokeRunner;

/// <summary>
/// --loop bench {acquire|release|status} : lock coopératif du banc de test.
/// Empêche 2 collègues de lancer 16 RIG chacun en même temps.
/// Fichier : %LOCALAPPDATA%\rig-wpf-testviewer\loop\.bench-lock
/// </summary>
internal static class LoopBench
{
    private static string LockPath() => Path.Combine(LoopArtifacts.LoopRoot(), ".bench-lock");

    public static int Execute(string[] args)
    {
        string sub = args.SkipWhile(a => !a.Equals("bench", StringComparison.OrdinalIgnoreCase))
                         .Skip(1).FirstOrDefault(a => !a.StartsWith("-"));
        switch ((sub ?? "status").ToLowerInvariant())
        {
            case "acquire": return Acquire();
            case "release": return Release();
            case "status":  return Status();
            default:
                Console.WriteLine("Usage : --loop bench {acquire|release|status}");
                return 64;
        }
    }

    private static int Acquire()
    {
        string path = LockPath();
        if (File.Exists(path) && !IsExpired(path))
        {
            Console.WriteLine("⛔ Banc OCCUPÉ — " + File.ReadAllText(path).Trim());
            return 1;
        }
        string who = Environment.UserName;
        string until = DateTime.Now.AddHours(2).ToString("o");
        File.WriteAllText(path, $"{who} jusqu'à {until}");
        Console.WriteLine($"✓ Banc acquis par {who} jusqu'à {until}");
        return 0;
    }

    private static int Release()
    {
        string path = LockPath();
        if (File.Exists(path)) File.Delete(path);
        Console.WriteLine("✓ Banc libéré");
        return 0;
    }

    private static int Status()
    {
        string path = LockPath();
        if (!File.Exists(path) || IsExpired(path)) { Console.WriteLine("✓ Banc LIBRE"); return 0; }
        Console.WriteLine("⛔ Banc OCCUPÉ — " + File.ReadAllText(path).Trim());
        return 1;
    }

    // Le lock contient « …jusqu'à <ISO> » ; périmé si la date est dépassée.
    private static bool IsExpired(string path)
    {
        try
        {
            string content = File.ReadAllText(path);
            int i = content.LastIndexOf("jusqu'à ", StringComparison.Ordinal);
            if (i < 0) return false;
            string iso = content.Substring(i + 8).Trim();
            return DateTime.TryParse(iso, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt) && dt < DateTime.Now;
        }
        catch { return false; }
    }
}
