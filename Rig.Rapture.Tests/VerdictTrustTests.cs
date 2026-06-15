using System.Text.Json;
using Rig.Wpf.Kbis.SmokeRunner;
using Rig.Wpf.Kbis.TestViewer.Services;
using Xunit;

namespace Rig.Rapture.Tests
{
    /// <summary>
    /// Verdict de confiance (fail-closed) — contrats PURS, hors RIG/DEV.
    /// I1 : le fichier résultat souverain du worker (nom + JSON, porteur du run-stamp + preuve positive
    /// d'assertions). I3 (à venir) : la fonction de verdict de l'orchestrateur (fichier manquant /
    /// run-stamp périmé / 0 assertion => ROUGE). Le filtre signal-cmd cible ce nom de classe.
    /// </summary>
    public class VerdictTrustTests
    {
        // ── I1 — nom du fichier résultat : porte scenarioId ET run-stamp (corrélation au run) ──
        [Fact]
        public void ScenarioResultFileName_porte_scenario_et_runstamp()
            => Assert.Equal("result-cas-a-20260615-120000.json",
                Observability.ScenarioResultFileName("cas-a", "20260615-120000"));

        [Fact]
        public void ScenarioResultFileName_deux_runstamps_donnent_deux_noms_distincts()
            => Assert.NotEqual(
                Observability.ScenarioResultFileName("cas-a", "stampA"),
                Observability.ScenarioResultFileName("cas-a", "stampB"));

        // ── I1 — JSON résultat : format machine-lisible exact ──
        [Fact]
        public void ScenarioResultJson_pass_format_exact()
            => Assert.Equal(
                "{\"schema\":1,\"scenario_id\":\"cas-a\",\"run_stamp\":\"20260615-120000\",\"status\":\"PASS\","
                + "\"exit_code\":0,\"assertions_run\":2,\"assertions_pass\":2,\"steps_passed\":5,"
                + "\"steps_failed\":0,\"steps_skipped\":1,\"duration_ms\":1234}",
                Observability.ScenarioResultJson("cas-a", "20260615-120000", failed: false,
                    assertionsRun: 2, assertionsPass: 2, stepsPassed: 5, stepsFailed: 0, stepsSkipped: 1,
                    exitCode: 0, durationMs: 1234));

        [Fact]
        public void ScenarioResultJson_failed_donne_status_FAIL()
        {
            var json = Observability.ScenarioResultJson("cas-x", "s", failed: true,
                assertionsRun: 3, assertionsPass: 2, stepsPassed: 4, stepsFailed: 1, stepsSkipped: 0,
                exitCode: 1, durationMs: 10);
            Assert.Contains("\"status\":\"FAIL\"", json);
            Assert.Contains("\"exit_code\":1", json);
        }

        // ── I1 — le JSON émis est RÉELLEMENT parsable par le consommateur (System.Text.Json côté TestViewer) ──
        [Fact]
        public void ScenarioResultJson_est_parsable_et_round_trip_les_champs()
        {
            var json = Observability.ScenarioResultJson("cas-b-multi", "20260615-120000", failed: false,
                assertionsRun: 1, assertionsPass: 1, stepsPassed: 6, stepsFailed: 0, stepsSkipped: 2,
                exitCode: 0, durationMs: 4567);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal("cas-b-multi", root.GetProperty("scenario_id").GetString());
            Assert.Equal("20260615-120000", root.GetProperty("run_stamp").GetString());
            Assert.Equal("PASS", root.GetProperty("status").GetString());
            Assert.Equal(0, root.GetProperty("exit_code").GetInt32());
            Assert.Equal(1, root.GetProperty("assertions_run").GetInt32());
            Assert.Equal(1, root.GetProperty("assertions_pass").GetInt32());
            Assert.Equal(4567, root.GetProperty("duration_ms").GetInt64());
        }

        // ── I1 — échappement JSON (un scenarioId/runStamp exotique ne casse pas le JSON) ──
        [Fact]
        public void JsonStr_echappe_quote_et_backslash()
            => Assert.Equal("\"a\\\"b\\\\c\"", Observability.JsonStr("a\"b\\c"));

        [Fact]
        public void ScenarioResultJson_reste_parsable_avec_un_scenarioId_a_guillemet()
        {
            var json = Observability.ScenarioResultJson("cas \"quote\"", "s", failed: false,
                assertionsRun: 0, assertionsPass: 0, stepsPassed: 1, stepsFailed: 0, stepsSkipped: 0,
                exitCode: 0, durationMs: 1);
            using var doc = JsonDocument.Parse(json);  // ne doit pas jeter
            Assert.Equal("cas \"quote\"", doc.RootElement.GetProperty("scenario_id").GetString());
        }

        // ─────────────────────────────────────────────────────────────────────────
        // I3 — LE CŒUR : verdict FAIL-CLOSED de l'orchestrateur. Injection de faute :
        // chaque trou de faux-vert doit produire NON-vert ; seul le cas complet est vert.
        // Le JSON de fixture est produit PAR le writer worker réel (contrat de bout en bout).
        // ─────────────────────────────────────────────────────────────────────────
        private const string Stamp = "20260615-120000-000";

        private static string GoodResult(int aRun = 2, int aPass = 2, int exit = 0, bool failed = false, string stamp = Stamp)
            => Observability.ScenarioResultJson("cas-a", stamp, failed: failed,
                assertionsRun: aRun, assertionsPass: aPass, stepsPassed: 5, stepsFailed: failed ? 1 : 0,
                stepsSkipped: 0, exitCode: exit, durationMs: 100);

        // #1 — clic avalé / worker jamais lancé / crash → AUCUN fichier → ROUGE.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Evaluate_fichier_absent_donne_UNVERIFIED_jamais_vert(string json)
        {
            var v = VerdictTrust.Evaluate(json, Stamp, workerExitCode: 0);
            Assert.False(v.Verified);
            Assert.Equal("UNVERIFIED", v.Status);
        }

        // Contrôle POSITIF : le cas complet (fichier frais + exit 0 + PASS + assertions passées) EST vert.
        [Fact]
        public void Evaluate_cas_complet_est_vert()
        {
            var v = VerdictTrust.Evaluate(GoodResult(), Stamp, workerExitCode: 0);
            Assert.True(v.Verified);
            Assert.Equal("PASS", v.Status);
            Assert.Equal(2, v.AssertionsRun);
        }

        // #3 — fichier d'un run ANTÉRIEUR (run-stamp ≠ attendu) → ROUGE. Contrôle négatif du cas vert :
        // exactement le même fichier, seul le stamp attendu change → bascule de vert à non-vert.
        [Fact]
        public void Evaluate_runstamp_perime_donne_UNVERIFIED()
        {
            var v = VerdictTrust.Evaluate(GoodResult(), expectedRunStamp: "un-autre-run", workerExitCode: 0);
            Assert.False(v.Verified);
            Assert.Equal("UNVERIFIED", v.Status);
            Assert.Contains("périmé", v.Detail);
        }

        // #2 — exit 0 mais AUCUNE assertion sémantique → exit 0 ne prouve rien → ROUGE.
        [Fact]
        public void Evaluate_zero_assertion_donne_UNVERIFIED_meme_si_exit0_et_PASS()
        {
            var v = VerdictTrust.Evaluate(GoodResult(aRun: 0, aPass: 0), Stamp, workerExitCode: 0);
            Assert.False(v.Verified);
            Assert.Equal("UNVERIFIED", v.Status);
        }

        // Worker en échec prouvé : exit non nul → FAIL (≠ UNVERIFIED).
        [Fact]
        public void Evaluate_exit_non_nul_donne_FAIL()
        {
            var v = VerdictTrust.Evaluate(GoodResult(exit: 1, failed: true), Stamp, workerExitCode: 1);
            Assert.False(v.Verified);
            Assert.Equal("FAIL", v.Status);
        }

        // Une assertion a échoué (pass < run) → FAIL même si le worker prétend PASS (défense).
        [Fact]
        public void Evaluate_assertion_echouee_donne_FAIL()
        {
            var v = VerdictTrust.Evaluate(GoodResult(aRun: 3, aPass: 2), Stamp, workerExitCode: 0);
            Assert.False(v.Verified);
            Assert.Equal("FAIL", v.Status);
        }

        // JSON corrompu : on ne peut PAS prouver le vert → ROUGE (jamais un faux-vert sur fichier illisible).
        [Fact]
        public void Evaluate_json_corrompu_donne_UNVERIFIED()
        {
            var v = VerdictTrust.Evaluate("{ ceci n'est pas du json", Stamp, workerExitCode: 0);
            Assert.False(v.Verified);
            Assert.Equal("UNVERIFIED", v.Status);
        }

        // ─────────── M2 — parsing run_stamp du sentinel (gate driver anti faux-vert chemin --drive) ──────
        [Fact]
        public void ParseSentinelRunStamp_extrait_le_champ()
            => Assert.Equal("20260615-120000-000",
                Observability.ParseSentinelRunStamp(
                    "2026-06-15T10:00:00.0000000Z|passed=3|failed=0|elapsed_ms=1200|run_stamp=20260615-120000-000"));

        [Theory]
        [InlineData("2026-06-15T10:00:00Z|passed=3|failed=0|elapsed_ms=1200")] // ancien format sans run_stamp
        [InlineData("")]
        [InlineData(null)]
        public void ParseSentinelRunStamp_vide_si_absent(string payload)
            => Assert.Equal("", Observability.ParseSentinelRunStamp(payload));

        // Gate driver (predicat inline de WaitForSmokeCompletion) : sentinel run_stamp == baseline = perime.
        [Fact]
        public void Sentinel_meme_runstamp_que_baseline_est_perime()
        {
            var baseline = Observability.ParseSentinelRunStamp("ts|run_stamp=RUN_A");
            var current = Observability.ParseSentinelRunStamp("ts|run_stamp=RUN_A");
            bool freshStamp = string.IsNullOrEmpty(current) ? true : current != baseline;
            Assert.False(freshStamp);
        }

        [Fact]
        public void Sentinel_nouveau_runstamp_vaut_completion()
        {
            var baseline = Observability.ParseSentinelRunStamp("ts|run_stamp=RUN_A");
            var current = Observability.ParseSentinelRunStamp("ts|run_stamp=RUN_B");
            bool freshStamp = string.IsNullOrEmpty(current) ? true : current != baseline;
            Assert.True(freshStamp);
        }

        // ─────────── (b) Isolation batches concurrents — noms d'artefacts agrégés run-stampés ───────────
        [Fact]
        public void BatchResultFileName_runstampe()
            => Assert.Equal("last-batch-result.20260615-120000-000.json",
                Observability.BatchResultFileName("20260615-120000-000"));

        [Fact]
        public void BatchSentinelFileName_runstampe()
            => Assert.Equal("last-batch-end.20260615-120000-000.txt",
                Observability.BatchSentinelFileName("20260615-120000-000"));

        // Deux batches concurrents = deux noms de fichiers DISTINCTS => pas de clobber mutuel.
        [Fact]
        public void Deux_runstamps_donnent_des_artefacts_distincts()
        {
            Assert.NotEqual(Observability.BatchResultFileName("RUN_A"), Observability.BatchResultFileName("RUN_B"));
            Assert.NotEqual(Observability.BatchSentinelFileName("RUN_A"), Observability.BatchSentinelFileName("RUN_B"));
        }

        // Sans run_stamp → retombe sur le nom fixe partagé (rétrocompat legacy).
        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void Sans_runstamp_nom_fixe_legacy(string stamp)
        {
            Assert.Equal("last-batch-result.json", Observability.BatchResultFileName(stamp));
            Assert.Equal("last-batch-end.txt", Observability.BatchSentinelFileName(stamp));
        }
    }
}
