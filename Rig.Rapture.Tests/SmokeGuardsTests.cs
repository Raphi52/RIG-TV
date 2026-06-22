using Xunit;
using Rig.Wpf.Kbis.SmokeRunner;

namespace Rig.Rapture.Tests
{
    /// <summary>
    /// Repro xUnit du FAUX-GREEN du SmokeRunner : un run qui n'atteint jamais son resume
    /// (bail/crash/timeout -> log-box partielle, NI "✓ passed" NI "✗ failed") doit ECHOUER (fail-closed),
    /// pas passer en vert. xUnit-first : pas de smoke live (RIG/TestViewer).
    /// </summary>
    public class SmokeGuardsTests
    {
        // Resume d'un run ABOUTI sans echec (format de PrintSummaryAndExit / log-box VK).
        const string PassedClean = "  ✓ passed  5\n  ⊘ skipped 0\n  ✗ failed  0\n  ⏱ 51234 ms\n";
        // Resume d'un run ABOUTI avec echecs.
        const string WithFailures = "  ✓ passed  3\n  ⊘ skipped 0\n  ✗ failed  2\n";
        // Log-box d'un run INCOMPLET (RIG plante / WaitForSmokeCompletion bail) : aucun resume.
        const string Incomplete = "Launching RigClientAccueil...\n[VK] etape 2 en cours...\n(pas de resume — bail/crash)\n";

        [Fact]
        public void AssertSmokeCompleted_passed_clean_does_not_throw()
        {
            SmokeGuards.AssertSmokeCompleted(PassedClean); // ne doit RIEN lever
        }

        [Fact]
        public void AssertSmokeCompleted_with_failures_throws()
        {
            Assert.ThrowsAny<System.Exception>(() => SmokeGuards.AssertSmokeCompleted(WithFailures));
        }

        [Fact]
        public void AssertSmokeCompleted_incomplete_run_throws()
        {
            // LE coeur du faux-green : un run incomplet DOIT echouer (rouge avec la logique d'origine).
            Assert.ThrowsAny<System.Exception>(() => SmokeGuards.AssertSmokeCompleted(Incomplete));
        }

        // --- Defaut trouve par le judge (Corrector) : discriminant a DEUX aiguilles ("✗ failed" vs "✗ failed  ") ---
        // Resume ABOUTI avec echecs mais UNE seule espace -> zone morte : juge complet ET propre = FAUX-GREEN.
        const string FailedSingleSpace = "  ✓ passed  3\n  ✗ failed 2\n";
        // "✗ failed" PARASITE hors-resume (pas de "✓ passed  N") -> doit etre vu INCOMPLET, pas complet-propre.
        const string StrayFailedNoSummary = "Erreur fatale: l etape ✗ failed a echoue avant le resume\n";

        [Fact]
        public void AssertSmokeCompleted_failure_single_space_throws()
        {
            Assert.ThrowsAny<System.Exception>(() => SmokeGuards.AssertSmokeCompleted(FailedSingleSpace));
        }

        [Fact]
        public void AssertSmokeCompleted_stray_failed_without_summary_throws()
        {
            Assert.ThrowsAny<System.Exception>(() => SmokeGuards.AssertSmokeCompleted(StrayFailedNoSummary));
        }

        // --- Durcissement judge cycle 2 (last-match) : un "✗ failed 0" parasite AVANT le vrai resume ---
        const string StrayZeroThenRealFail = "  ✗ failed 0 (ligne de detail amont)\n  ✓ passed  3\n  ✗ failed  2\n";

        [Fact]
        public void AssertSmokeCompleted_lastmatch_catches_real_failure_after_stray_zero()
        {
            // first-match aurait happe "✗ failed 0" -> faux-green ; last-match prend le vrai "✗ failed  2".
            Assert.ThrowsAny<System.Exception>(() => SmokeGuards.AssertSmokeCompleted(StrayZeroThenRealFail));
        }

        // --- Residuel (a) : AssertBatchCompleted (mode all-scenarios, marqueur RECAP) ---
        const string BatchOk = "...\nRECAP ALL SCENARIOS : 5 OK, 0 FAIL\n";
        const string BatchFail = "...\nRECAP ALL SCENARIOS : 3 OK, 2 FAIL\n";
        const string BatchIncomplete = "Launching worker 1...\n[scenario 2] en cours...\n(crash avant RECAP)\n";

        [Fact]
        public void AssertBatchCompleted_ok_does_not_throw()
        {
            SmokeGuards.AssertBatchCompleted(BatchOk);
        }

        [Fact]
        public void AssertBatchCompleted_with_fail_throws()
        {
            Assert.ThrowsAny<System.Exception>(() => SmokeGuards.AssertBatchCompleted(BatchFail));
        }

        [Fact]
        public void AssertBatchCompleted_incomplete_throws()
        {
            // LE residuel (a) : crash/bail avant RECAP => DOIT echouer (etait un faux-green inline dans Program.cs).
            Assert.ThrowsAny<System.Exception>(() => SmokeGuards.AssertBatchCompleted(BatchIncomplete));
        }
    }
}
