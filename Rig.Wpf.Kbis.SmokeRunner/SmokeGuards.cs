using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Rig.Wpf.Kbis.SmokeRunner
{
    /// <summary>
    /// Gardes de complétude d'un smoke piloté (extrait des modes --drive-testviewer-*). PURS : n'inspectent
    /// que le texte FINAL de la log-box (ce que renvoie TestViewerDriver.WaitForSmokeCompletion). Lèvent si le
    /// run n'a jamais atteint son résumé (faux-green sur bail/crash/timeout) OU s'il a échoué.
    /// Testables en xUnit (Rig.Rapture.Tests) sans smoke live.
    /// </summary>
    public static class SmokeGuards
    {
        // UNE aiguille tolerante aux espaces + un CHIFFRE exige (un "✗ failed" parasite ne compte pas).
        private static readonly Regex FailedLine = new Regex(@"✗\s*failed\s+(\d+)", RegexOptions.Compiled);
        private static readonly Regex PassedLine = new Regex(@"✓\s*passed\s+\d+", RegexOptions.Compiled);

        /// <summary>Mode single-scenario. Resume attendu : "✓ passed N" + "✗ failed N".</summary>
        public static void AssertSmokeCompleted(string final)
        {
            final = final ?? "";
            // last-match (durcissement judge cycle 2) : le resume est en FIN ; un "✗ failed N" parasite en
            // amont (ligne de detail, exception) ne masque plus l'echec reel.
            var failed = FailedLine.Matches(final).Cast<Match>().LastOrDefault();
            if (failed == null || !PassedLine.IsMatch(final))
                throw new Exception("Smoke INCOMPLET : resume (passed/failed) absent de la log-box — bail/crash/timeout (faux-green evite)");
            int n;
            if (!int.TryParse(failed.Groups[1].Value, out n))   // fail-safe : compteur illisible => on echoue, pas d'OverflowException nue
                throw new Exception("Smoke : compteur d'echecs illisible (resume corrompu) — fail-closed");
            if (n > 0)
                throw new Exception("Smoke termine avec au moins 1 echec (voir stdout ci-dessus)");
        }

        /// <summary>Mode batch all-scenarios. Completude = "RECAP ALL SCENARIOS" present ; echec = "FAIL" hors "0 FAIL".</summary>
        public static void AssertBatchCompleted(string final)
        {
            final = final ?? "";
            var recapIdx = final.IndexOf("RECAP ALL SCENARIOS", StringComparison.Ordinal);
            if (recapIdx < 0)   // residuel (a) : recapIdx<0 = crash/bail avant le resume => etait un faux-green
                throw new Exception("Batch INCOMPLET : 'RECAP ALL SCENARIOS' absent — crash/bail avant le resume (faux-green evite)");
            var recap = final.Substring(recapIdx);
            if (recap.Contains("FAIL") && !recap.Contains("0 FAIL"))
                throw new Exception("Batch termine avec echec(s) — voir RECAP");
        }
    }
}
