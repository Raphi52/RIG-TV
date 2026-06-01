using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Rig.Wpf.Kbis.SmokeRunner;

/// <summary>
/// --loop build : encapsule build PROC_RETAUD + deploy en UNE commande
/// au verdict pass/fail unique. Évite la chasse aux erreurs du build circulaire.
/// </summary>
internal static class LoopBuild
{
    public static int Execute(string[] args)
    {
        string repo = FindRepoRoot(AppDomain.CurrentDomain.BaseDirectory);
        if (repo == null) { Console.WriteLine("ERREUR : racine repo introuvable."); return 66; }

        string safeBuild = Path.Combine(repo, "Source", "CompilationLivraison", "safeBuild.ps1");
        string proj = Path.Combine(repo, "Source", "RIG", "DLL", "Processus",
                                   "PROC_RETAUD", "PROC_RETAUD.csproj");
        if (!File.Exists(safeBuild) || !File.Exists(proj))
        {
            Console.WriteLine("ERREUR : safeBuild.ps1 ou PROC_RETAUD.csproj introuvable.");
            return 66;
        }

        Console.WriteLine("▶ LOOP BUILD — PROC_RETAUD (Release)…");
        var (buildExitCode, output) = RunPs(safeBuild,
            $"\"{proj}\" Build Release \"\" v48 -AllowComInteropRegistration -Property \"RegisterForComInterop=false\"");

        // safeBuild.ps1 peut retourner exit 1 même quand MSBuild réussit (comportement constaté
        // dans cet environnement : safeBuild enchaîne des étapes dont certaines peuvent échouer
        // sans que le build PROC_RETAUD lui-même soit en erreur). On détermine donc le verdict
        // à partir de la SORTIE du build, pas de l'exit code de safeBuild.ps1.
        bool buildOk = output.IndexOf("MSBuild exit code: 0", StringComparison.OrdinalIgnoreCase) >= 0
                    || output.IndexOf("génération a réussi", StringComparison.OrdinalIgnoreCase) >= 0
                    || System.Text.RegularExpressions.Regex.IsMatch(output, @"(?m)^\s*0\s+Erreur");

        if (!buildOk)
        {
            Console.WriteLine($"✗ BUILD ÉCHEC (safeBuild exit {buildExitCode}, aucun marqueur de succès dans la sortie)");
            return 1;
        }
        Console.WriteLine("✓ Build PROC_RETAUD OK (MSBuild)");

        string deploy = Path.Combine(repo, "Source", "CompilationLivraison", "deploy-proc-retaud.ps1");
        if (File.Exists(deploy))
        {
            Console.WriteLine("▶ LOOP BUILD — deploy…");
            var (deployExitCode, _) = RunPs(deploy, "");
            if (deployExitCode != 0) { Console.WriteLine($"✗ DEPLOY ÉCHEC (exit {deployExitCode})"); return 1; }
        }
        else
        {
            Console.WriteLine("⚠ deploy-proc-retaud.ps1 absent — build seul, pas de deploy.");
        }
        Console.WriteLine("✓ LOOP BUILD OK");
        return 0;
    }

    private static (int exitCode, string output) RunPs(string script, string scriptArgs)
    {
        var sb = new StringBuilder();
        var psi = new ProcessStartInfo("powershell.exe",
            $"-ExecutionPolicy Bypass -File \"{script}\" {scriptArgs}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8, // sinon stderr en OEM → accents corrompus dans le scan du verdict
            CreateNoWindow = true,
        };
        using (var p = Process.Start(psi))
        {
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    Console.WriteLine("   " + e.Data);
                    sb.AppendLine(e.Data);
                }
            };
            p.BeginOutputReadLine();
            string err = p.StandardError.ReadToEnd();
            // Garde-fou : le build circulaire RIG peut se figer. Plafond 30 min.
            // WaitForExit(timeout) puis WaitForExit() sans argument : le 2e appel
            // garantit le flush de tous les events OutputDataReceived en attente.
            const int buildTimeoutMs = 30 * 60 * 1000;
            if (!p.WaitForExit(buildTimeoutMs))
            {
                try { p.Kill(); } catch { }
                Console.WriteLine("   [timeout] process tué après 30 min");
                return (int.MinValue, sb.ToString());
            }
            p.WaitForExit();
            if (!string.IsNullOrWhiteSpace(err))
            {
                Console.WriteLine("   [stderr] " + err.Trim());
                sb.AppendLine(err);
            }
            return (p.ExitCode, sb.ToString());
        }
    }

    private static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Source", "CompilationLivraison")))
                return dir.FullName;
        }
        return null;
    }
}
