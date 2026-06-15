using System;

namespace Rig.Wpf.Kbis.SmokeRunner
{
    /// <summary>
    /// Helpers d'observabilité PURS (sans I/O), extraits pour être testables en xUnit hors RIG/DEV :
    /// format des noms de self-snap (#4, partagé par les 2 sites d'écriture) et des lignes
    /// snap-index.jsonl (#3). Source unique de vérité du format → les 2 sites ne peuvent plus diverger.
    /// </summary>
    public static class Observability
    {
        /// <summary>#4 — nom de fichier self-snap : millisecondes dans le timestamp pour désambigüer
        /// deux snaps de la même seconde ; seq sur 4 chiffres = clé de corrélation stable ([snap=NNNN]).</summary>
        public static string SnapFileName(int seq, DateTime ts) => $"snap-{ts:HHmmssfff}-{seq:D4}.png";

        /// <summary>#3 — ligne JSONL de snap-index : objet {seq, ts (ms), file} sur une ligne.</summary>
        public static string SnapIndexLine(int seq, DateTime ts, string fileName) =>
            $"{{\"seq\":{seq},\"ts\":\"{ts:HH:mm:ss.fff}\",\"file\":\"{fileName}\"}}";

        // ── Verdict de confiance (fail-closed) ───────────────────────────────────
        // Le worker écrit SON PROPRE fichier résultat souverain, porteur du run-stamp (nonce du run)
        // et d'une preuve POSITIVE qu'une assertion sémantique a tourné. L'orchestrateur (TestViewer)
        // agrège ces fichiers : fichier manquant / run-stamp qui ne matche pas / 0 assertion = ROUGE.
        // SmokeRunner est découplé de TestViewer (aucune ProjectReference) → la convention est partagée
        // PAR LA CHAÎNE DE CARACTÈRES du path + du JSON, pas par du code commun. Ces helpers PURS sont
        // la source unique de vérité de ce contrat (testés en xUnit hors RIG/DEV).

        /// <summary>Nom du fichier résultat souverain : <c>result-&lt;scenarioId&gt;-&lt;runStamp&gt;.json</c>.
        /// Le run-stamp dans le nom (et le dossier parent) corrèle le fichier à CE run → un fichier
        /// laissé par un run antérieur ne peut pas être lu comme courant (ferme le faux-vert « sentinel
        /// périmé »).</summary>
        public static string ScenarioResultFileName(string scenarioId, string runStamp) =>
            $"result-{scenarioId}-{runStamp}.json";

        /// <summary>JSON résultat souverain d'un scénario (une ligne, sans dépendance de sérialisation).
        /// <paramref name="assertionsRun"/>/<paramref name="assertionsPass"/> = preuve positive qu'un
        /// check sémantique (expected vs actual) a réellement tourné — pas un simple step de plomberie.</summary>
        public static string ScenarioResultJson(string scenarioId, string runStamp, bool failed,
            int assertionsRun, int assertionsPass, int stepsPassed, int stepsFailed, int stepsSkipped,
            int exitCode, long durationMs)
        {
            var status = failed ? "FAIL" : "PASS";
            return "{"
                + "\"schema\":1,"
                + $"\"scenario_id\":{JsonStr(scenarioId)},"
                + $"\"run_stamp\":{JsonStr(runStamp)},"
                + $"\"status\":\"{status}\","
                + $"\"exit_code\":{exitCode},"
                + $"\"assertions_run\":{assertionsRun},"
                + $"\"assertions_pass\":{assertionsPass},"
                + $"\"steps_passed\":{stepsPassed},"
                + $"\"steps_failed\":{stepsFailed},"
                + $"\"steps_skipped\":{stepsSkipped},"
                + $"\"duration_ms\":{durationMs}"
                + "}";
        }

        /// <summary>Nom du JSON verdict AGRÉGÉ run-stampé : <c>last-batch-result.&lt;runStamp&gt;.json</c>
        /// (isolation des batches concurrents — chaque batch a sa copie, le « latest » fixe reste pour les
        /// lecteurs legacy/diff). Sans runStamp → retombe sur le nom fixe partagé.</summary>
        public static string BatchResultFileName(string runStamp) =>
            string.IsNullOrEmpty(runStamp) ? "last-batch-result.json" : $"last-batch-result.{runStamp}.json";

        /// <summary>Nom du sentinel fin-de-batch run-stampé : <c>last-batch-end.&lt;runStamp&gt;.txt</c>.
        /// Permet à un driver d'attendre LE sentinel de SON run, pas celui qu'un batch concurrent a clobberé.</summary>
        public static string BatchSentinelFileName(string runStamp) =>
            string.IsNullOrEmpty(runStamp) ? "last-batch-end.txt" : $"last-batch-end.{runStamp}.txt";

        /// <summary>Extrait le champ <c>run_stamp=</c> du payload sentinel <c>last-batch-end.txt</c>
        /// (format <c>ts|passed=N|failed=N|elapsed_ms=N|run_stamp=X</c>), ou "" si absent. Pur (testable) :
        /// permet au driver d'exiger que la complétion corresponde au run COURANT, pas à un sentinel périmé.</summary>
        public static string ParseSentinelRunStamp(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return "";
            foreach (var part in payload.Split('|'))
            {
                var p = part.Trim();
                if (p.StartsWith("run_stamp=", StringComparison.Ordinal))
                    return p.Substring("run_stamp=".Length).Trim();
            }
            return "";
        }

        /// <summary>Échappe une chaîne en littéral JSON entre guillemets (quote/backslash/contrôles).</summary>
        public static string JsonStr(string s)
        {
            if (s == null) return "null";
            var sb = new System.Text.StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }

    /// <summary>#1 — multiplexe Console.Out vers la console ET un fichier log co-localisé, pour que TOUT
    /// mode standalone laisse une trace lisible. L'écriture fichier est best-effort (un échec disque ne
    /// casse jamais le run) ; la console n'est jamais best-effort (sortie primaire). Extrait de Program
    /// pour être testable en xUnit (vérifie que rien n'est silencieusement perdu côté fichier).</summary>
    public sealed class TeeTextWriter : System.IO.TextWriter
    {
        private readonly System.IO.TextWriter _console;
        private readonly System.IO.TextWriter _file;
        public TeeTextWriter(System.IO.TextWriter console, System.IO.TextWriter file) { _console = console; _file = file; }
        public override System.Text.Encoding Encoding => _console.Encoding;
        public override void Write(char value) { _console.Write(value); try { _file.Write(value); } catch { } }
        public override void Write(string value) { _console.Write(value); try { _file.Write(value); } catch { } }
        public override void WriteLine(string value) { _console.WriteLine(value); try { _file.WriteLine(value); } catch { } }
        public override void Flush() { _console.Flush(); try { _file.Flush(); } catch { } }
    }
}
