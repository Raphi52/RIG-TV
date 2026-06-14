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
