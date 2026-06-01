using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Xunit;

namespace Rig.Wpf.IntegrationTests.Bridges;

/// <summary>
/// Test bout-en-bout du bridge legacy : on lance Rig.Wpf.Shell.exe avec une
/// config qui force le chargement de PROC_TESTNLH en mode legacy. On vérifie
/// dans les logs Serilog :
///  1. l'installation de l'adapter dans FormAccueil.FrmAccueil
///  2. le chargement effectif du plugin legacy
///  3. au moins un appel intercepté par l'adapter (preuve de communication).
/// </summary>
[Trait("Category", "Integration")]
public class LegacyBridgeRoundTripTests
{
    private static string ShellExePath
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            @"..\..\..\..\Rig.Wpf.Shell\bin\Release\net48\Rig.Wpf.Shell.exe"));

    private static string ShellAppSettingsPath
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            @"..\..\..\..\Rig.Wpf.Shell\bin\Release\net48\appsettings.json"));

    private const string LogDirectory = @"C:\rig\logs";

    [SkippableFact]
    public void Shell_BridgesLegacyPlugin_AndInterceptsCallbacks()
    {
        Skip.IfNot(File.Exists(ShellExePath),
            $"Rig.Wpf.Shell.exe absent — build d'abord. ({ShellExePath})");
        Skip.IfNot(Directory.Exists(@"C:\rig\Bin Processus"),
            "C:\\rig\\Bin Processus absent — environnement non provisionné.");
        Skip.IfNot(Directory.Exists(@"C:\rig\Bin Dot Net Gac"),
            "C:\\rig\\Bin Dot Net Gac absent — environnement non provisionné.");

        // Forcer TESTNLH en mode legacy le temps du test.
        var originalConfig = File.ReadAllText(ShellAppSettingsPath);
        File.WriteAllText(ShellAppSettingsPath,
            "{\"RigWpf\":{"
            + "\"PluginDirectory\":\"C:\\\\rig\\\\Bin Processus\","
            + "\"DisableNativeFor\":[\"TESTNLH\"],"
            + "\"PreOpenTabs\":[\"TESTNLH\"]"
            + "}}");

        // Nettoyage logs pour ne lire que ceux du run en cours.
        if (Directory.Exists(LogDirectory))
        {
            foreach (var f in Directory.GetFiles(LogDirectory, "rig-wpf-shell-*.log"))
            {
                try { File.Delete(f); } catch { /* best-effort */ }
            }
        }

        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = ShellExePath,
                UseShellExecute = false
            });
            process.Should().NotBeNull();
            Thread.Sleep(TimeSpan.FromSeconds(5));
        }
        finally
        {
            try
            {
                if (process is not null && !process.HasExited) process.Kill();
            }
            catch { /* best-effort */ }
            // Restaurer la config originale même si le test échoue.
            File.WriteAllText(ShellAppSettingsPath, originalConfig);
        }

        var latestLog = Directory.GetFiles(LogDirectory, "rig-wpf-shell-*.log")
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();
        latestLog.Should().NotBeNull("le shell doit avoir produit un fichier log");

        // Le shell vient d'être tué mais Serilog peut encore détenir le handle.
        // FileShare.ReadWrite permet de lire malgré le lock résiduel.
        using var stream = new FileStream(latestLog!, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var logContent = reader.ReadToEnd();
        logContent.Should().Contain("WpfFormAccueilLegacyAdapter installé",
            "l'adapter doit être posé dans FormAccueil.FrmAccueil au démarrage");
        logContent.Should().Contain("Chargement plugin legacy TESTNLH",
            "le plugin legacy TESTNLH doit être chargé en mode cohabitation");
        // Au moins un appel doit être intercepté par l'adapter (typiquement
        // NotifierLancementArret côté FORM_TESTNLH ou autre méthode du plugin).
        logContent.Should().Contain("appelé sur l'adapter WPF",
            "le plugin legacy doit avoir invoqué au moins une méthode IFormAccueil sur notre adapter");
    }
}
