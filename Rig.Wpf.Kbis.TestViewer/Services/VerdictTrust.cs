using System;
using System.Text.Json;

namespace Rig.Wpf.Kbis.TestViewer.Services
{
    /// <summary>Verdict agrégé d'un scénario par l'orchestrateur.</summary>
    public sealed class ScenarioVerdict
    {
        /// <summary>VRAI uniquement si la preuve positive est COMPLÈTE. Toute incertitude => faux.</summary>
        public bool Verified;
        /// <summary>"PASS" | "FAIL" | "UNVERIFIED" (UNVERIFIED = on ne peut PAS prouver le résultat).</summary>
        public string Status;
        public int AssertionsRun;
        public int AssertionsPass;
        public string Detail;
    }

    /// <summary>
    /// Cœur du verdict FAIL-CLOSED (Option C). Décide PUREMENT (sans I/O, testable hors RIG/DEV) si un
    /// scénario est vert, à partir du fichier résultat souverain écrit par le worker. Principe : le vert
    /// exige une preuve POSITIVE complète ; toute absence/incohérence => ROUGE. Ferme les 3 faux-verts :
    ///   #1 clic avalé / worker jamais lancé → aucun fichier → UNVERIFIED ;
    ///   #2 exit 0 mais 0 assertion sémantique → UNVERIFIED (exit 0 ne prouve rien) ;
    ///   #3 fichier d'un run antérieur → run-stamp ≠ attendu → UNVERIFIED.
    /// </summary>
    public static class VerdictTrust
    {
        public static ScenarioVerdict Evaluate(string resultJsonOrNull, string expectedRunStamp, int workerExitCode)
        {
            // #1 — aucun fichier : le worker n'a pas atteint sa fin normale (clic avalé, crash, jamais lancé).
            if (string.IsNullOrWhiteSpace(resultJsonOrNull))
                return new ScenarioVerdict
                {
                    Verified = false,
                    Status = "UNVERIFIED",
                    Detail = $"UNVERIFIED: aucun fichier résultat worker (exit observé={workerExitCode})"
                };

            try
            {
                using var doc = JsonDocument.Parse(resultJsonOrNull);
                var root = doc.RootElement;
                var stamp = root.GetProperty("run_stamp").GetString();
                var status = root.GetProperty("status").GetString();
                var exit = root.GetProperty("exit_code").GetInt32();
                var aRun = root.GetProperty("assertions_run").GetInt32();
                var aPass = root.GetProperty("assertions_pass").GetInt32();

                // #3 — run-stamp périmé : fichier laissé par un run antérieur, jamais relu comme courant.
                if (!string.Equals(stamp, expectedRunStamp, StringComparison.Ordinal))
                    return new ScenarioVerdict
                    {
                        Verified = false, Status = "UNVERIFIED", AssertionsRun = aRun, AssertionsPass = aPass,
                        Detail = $"UNVERIFIED: run-stamp périmé (fichier='{stamp}' attendu='{expectedRunStamp}')"
                    };

                // Worker en échec : exit non nul ou status FAIL auto-déclaré.
                if (exit != 0 || status != "PASS")
                    return new ScenarioVerdict
                    {
                        Verified = false, Status = "FAIL", AssertionsRun = aRun, AssertionsPass = aPass,
                        Detail = $"FAIL: exit={exit} status={status} assert={aPass}/{aRun}"
                    };

                // Une assertion a échoué (défense : normalement déjà exit!=0).
                if (aPass < aRun)
                    return new ScenarioVerdict
                    {
                        Verified = false, Status = "FAIL", AssertionsRun = aRun, AssertionsPass = aPass,
                        Detail = $"FAIL: assertion KO ({aPass}/{aRun})"
                    };

                // #2 — exit 0 mais AUCUNE assertion sémantique n'a tourné : exit 0 ne prouve rien.
                if (aRun == 0)
                    return new ScenarioVerdict
                    {
                        Verified = false, Status = "UNVERIFIED", AssertionsRun = 0, AssertionsPass = 0,
                        Detail = "UNVERIFIED: 0 assertion sémantique (scénario sans expected-* ?) — exit 0 ne prouve rien"
                    };

                // VERT : fichier frais (run-stamp OK) + exit 0 + status PASS + ≥1 assertion, toutes passées.
                return new ScenarioVerdict
                {
                    Verified = true, Status = "PASS", AssertionsRun = aRun, AssertionsPass = aPass,
                    Detail = $"verified exit=0 assert={aPass}/{aRun}"
                };
            }
            catch (Exception ex)
            {
                // JSON corrompu/incomplet : on ne peut PAS prouver le vert => ROUGE.
                return new ScenarioVerdict
                {
                    Verified = false, Status = "UNVERIFIED",
                    Detail = $"UNVERIFIED: fichier résultat illisible ({ex.GetType().Name})"
                };
            }
        }
    }
}
