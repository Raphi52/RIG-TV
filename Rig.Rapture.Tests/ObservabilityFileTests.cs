using System;
using System.IO;
using Rig.Wpf.Kbis.TestViewer.Services;
using Xunit;

namespace Rig.Rapture.Tests
{
    /// <summary>
    /// Vérifie que #5 (worker.stdout.log) et #6 (phases.jsonl) écrivent RÉELLEMENT les fichiers
    /// co-localisés sous self-snaps/&lt;RIG_RUN_STAMP&gt;/&lt;scenarioId&gt;/ via les vraies méthodes de prod
    /// (ObservabilityFiles). Pas de DEV ni de GUI : run stamp unique + cleanup. Couvre le seul bout de
    /// #5/#6 non testé par ailleurs (l'I/O de fichier + le calcul de chemin depuis RIG_RUN_STAMP).
    /// </summary>
    public class ObservabilityFileTests : IDisposable
    {
        private readonly string _stamp = "test-obs-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        private readonly string _prev;
        private readonly string _runDir;

        public ObservabilityFileTests()
        {
            _prev = Environment.GetEnvironmentVariable("RIG_RUN_STAMP");
            Environment.SetEnvironmentVariable("RIG_RUN_STAMP", _stamp);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _runDir = Path.Combine(local, "rig-wpf-testviewer", "self-snaps", _stamp);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("RIG_RUN_STAMP", _prev);
            try { if (Directory.Exists(_runDir)) Directory.Delete(_runDir, recursive: true); } catch { }
        }

        [Fact]
        public void WriteWorkerStdout_ecrit_le_log_co_localise()
        {
            ObservabilityFiles.WriteWorkerStdout("cas-x", "ligne 1\nligne 2 finale");
            var f = Path.Combine(_runDir, "cas-x", "worker.stdout.log");
            Assert.True(File.Exists(f), $"attendu : {f}");
            Assert.Contains("ligne 2 finale", File.ReadAllText(f));
        }

        [Fact]
        public void AppendPhase_ecrit_phases_jsonl_avec_snap_et_heure_extraits_de_la_ligne()
        {
            ObservabilityFiles.AppendPhase("cas-y", "  ✓ [14:06:40.123] [snap=0042] Open PROC_RETAUD", "OpenProcRetaud");
            var f = Path.Combine(_runDir, "cas-y", "phases.jsonl");
            Assert.True(File.Exists(f), $"attendu : {f}");
            Assert.Equal(
                "{\"ts\":\"14:06:40.123\",\"phase\":\"OpenProcRetaud\",\"snap_idx\":42}",
                File.ReadAllText(f).Trim());
        }

        [Fact]
        public void AppendPhase_ligne_sans_tag_donne_snap_moins_un()
        {
            ObservabilityFiles.AppendPhase("cas-narratif", "→ App ouverte post-login", "Login");
            var content = File.ReadAllText(Path.Combine(_runDir, "cas-narratif", "phases.jsonl"));
            Assert.Contains("\"phase\":\"Login\"", content);
            Assert.Contains("\"snap_idx\":-1", content);   // pas de [snap=] sur une ligne narrative → -1
        }

        [Fact]
        public void Sans_run_stamp_aucune_ecriture()
        {
            Environment.SetEnvironmentVariable("RIG_RUN_STAMP", null);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var leakScenario = "cas-leak-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var leakDir = Path.Combine(local, "rig-wpf-testviewer", "self-snaps", leakScenario);
            try
            {
                ObservabilityFiles.WriteWorkerStdout(leakScenario, "ne doit rien écrire");
                ObservabilityFiles.AppendPhase(leakScenario, "  ✓ [00:00:00.000] [snap=0001] x", "Login");
                Assert.False(Directory.Exists(leakDir), "no-op attendu quand RIG_RUN_STAMP est absent");
            }
            finally
            {
                Environment.SetEnvironmentVariable("RIG_RUN_STAMP", _stamp);
                try { if (Directory.Exists(leakDir)) Directory.Delete(leakDir, recursive: true); } catch { }
            }
        }
    }
}
