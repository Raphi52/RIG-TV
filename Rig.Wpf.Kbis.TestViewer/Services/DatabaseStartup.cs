// SPDX-License-Identifier: Proprietary
// Résolution + propagation de la connexion SQL choisie vers les workers smoke.
using System;

namespace Rig.Wpf.Kbis.TestViewer.Services;

/// <summary>
/// Résout la connexion SQL EFFECTIVE selon la précédence <b>CLI &gt; persisté &gt; défaut</b>
/// et la pose sur l'env var <c>RIG_LEGACY_CONNECTION</c> du PROCESS COURANT.
///
/// Pourquoi le process courant et pas chaque <c>ProcessStartInfo</c> : les workers
/// SmokeRunner héritent l'environnement du process parent (TestViewer) au moment du
/// <c>Process.Start</c>. Poser la var ICI propage donc le choix à TOUS les modes de smoke
/// (RAPTURE / KBIS / ALERTES / DCADEMAT) et à tout futur site de spawn, sans dupliquer la
/// logique sur chaque <c>psi</c>. SmokeRunner lit déjà <c>RIG_LEGACY_CONNECTION</c>
/// (LegacyDriver.cs / Program.cs) avec fallback dur sur SQL-DEV\DEV.
/// </summary>
public static class DatabaseStartup
{
    public const string EnvVar = "RIG_LEGACY_CONNECTION";

    // Surcharges parsées dans App.OnStartup (null = absent). One-shot pour le run courant.
    public static string? CliServer;
    public static string? CliDatabase;
    public static string? CliConnection;

    /// <summary>Connexion effective : <c>--connection=</c> &gt; (<c>--server=</c>/<c>--db=</c> | persisté) &gt; défaut.</summary>
    public static string Resolve(GlobalSettings s)
    {
        if (!string.IsNullOrWhiteSpace(CliConnection)) return CliConnection!;
        var server = !string.IsNullOrWhiteSpace(CliServer) ? CliServer! : s.DatabaseServer;
        var database = !string.IsNullOrWhiteSpace(CliDatabase) ? CliDatabase! : s.DatabaseName;
        return GlobalSettings.BuildConnectionString(server, database);
    }

    /// <summary>
    /// Pose <c>RIG_LEGACY_CONNECTION</c> sur le process courant → héritée par les workers.
    /// À appeler au démarrage (constructeur VM) ET après chaque sauvegarde des settings.
    /// L'écriture EXPLICITE écrase toute valeur héritée du shell parent, donc le choix UI
    /// gagne même si l'env var était déjà set par le poste.
    /// </summary>
    public static void Apply(GlobalSettings s)
        => Environment.SetEnvironmentVariable(EnvVar, Resolve(s), EnvironmentVariableTarget.Process);
}
