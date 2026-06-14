using System;
using Rig.Wpf.Kbis.SmokeRunner;
using Rig.Wpf.Kbis.TestViewer.Services;
using Xunit;

namespace Rig.Rapture.Tests
{
    /// <summary>
    /// Garde-fous des CONTRATS machine-lisibles de l'observabilité (hors RIG/DEV) :
    ///   #4 nom de self-snap (ms + seq D4) · #3 ligne snap-index.jsonl · #2/#6 tags [snap=]/[hh:mm:ss.fff]
    ///   et ligne phases.jsonl. Le round-trip #2→#6 vérifie que le format émis par le producteur
    ///   (Program.Stamp) est bien re-parsable par le consommateur (TestViewer).
    /// </summary>
    public class ObservabilityTests
    {
        private static readonly DateTime Ts = new DateTime(2026, 1, 1, 14, 6, 40, 123);

        // ── #4 — nom de self-snap : ms dans le timestamp + seq sur 4 chiffres ──
        [Fact]
        public void SnapFileName_inclut_les_ms_et_pad_le_seq_sur_4()
            => Assert.Equal("snap-140640123-0042.png", Observability.SnapFileName(42, Ts));

        [Fact]
        public void SnapFileName_deux_snaps_meme_seconde_seq_different_sont_distincts()
            => Assert.NotEqual(Observability.SnapFileName(1, Ts), Observability.SnapFileName(2, Ts));

        // ── #3 — ligne snap-index.jsonl ──
        [Fact]
        public void SnapIndexLine_format_json_exact()
            => Assert.Equal(
                "{\"seq\":42,\"ts\":\"14:06:40.123\",\"file\":\"snap-140640123-0042.png\"}",
                Observability.SnapIndexLine(42, Ts, "snap-140640123-0042.png"));

        // ── #2/#6 — parsing des tags [snap=] / [hh:mm:ss.fff] ──
        [Theory]
        [InlineData("  ✓ [14:06:40.123] [snap=0042] Open PROC_RETAUD", 42)]
        [InlineData("  ✗ [09:00:00.000] [snap=0007] Importer", 7)]
        [InlineData("→ App ouverte post-login", -1)]   // ligne narrative sans tag → pas de snap connu
        [InlineData("  ⊘ [10:00:00.000] [snap=----] formalite non actionnable", -1)] // pas de self-snap actif
        public void ParseSnapTag_extrait_l_index_ou_moins_un(string line, int expected)
            => Assert.Equal(expected, ObservabilityTags.ParseSnapTag(line));

        [Fact]
        public void ParseTimeTag_extrait_l_heure_quand_presente()
            => Assert.Equal("14:06:40.123", ObservabilityTags.ParseTimeTag("  ✓ [14:06:40.123] [snap=0042] x", "FALLBACK"));

        [Fact]
        public void ParseTimeTag_retombe_sur_le_fallback_si_absente()
            => Assert.Equal("00:00:00.000", ObservabilityTags.ParseTimeTag("→ ligne sans heure", "00:00:00.000"));

        // ── #6 — ligne phases.jsonl ──
        [Fact]
        public void PhaseLine_format_json_exact()
            => Assert.Equal(
                "{\"ts\":\"14:06:40.123\",\"phase\":\"OpenProcRetaud\",\"snap_idx\":42}",
                ObservabilityTags.PhaseLine("14:06:40.123", "OpenProcRetaud", 42));

        // ── Round-trip #2 → #6 : une ligne t=telle que Program.Stamp la produit est re-corrélable ──
        [Fact]
        public void RoundTrip_ligne_event_taggee_redonne_snap_et_heure()
        {
            // Format émis par SmokeRunner.Program.Stamp() : "[hh:mm:ss.fff] [snap=NNNN] "
            var emitted = $"  ✓ [{Ts:HH:mm:ss.fff}] [snap={42:D4}] Sélection audience";
            Assert.Equal(42, ObservabilityTags.ParseSnapTag(emitted));
            Assert.Equal("14:06:40.123", ObservabilityTags.ParseTimeTag(emitted, "FALLBACK"));
        }

        // ── #1 — TeeTextWriter : rien n'est silencieusement perdu côté fichier, la console reste primaire ──
        [Fact]
        public void TeeTextWriter_ecrit_sur_la_console_ET_le_fichier()
        {
            var console = new System.IO.StringWriter();
            var file = new System.IO.StringWriter();
            using (var tee = new TeeTextWriter(console, file))
            {
                tee.Write("a");
                tee.Write("bc");
                tee.WriteLine("def");
            }
            Assert.Equal(console.ToString(), file.ToString());        // les deux flux reçoivent exactement la même chose
            Assert.Contains("abcdef", file.ToString());
        }

        [Fact]
        public void TeeTextWriter_un_echec_fichier_ne_casse_jamais_la_console()
        {
            var console = new System.IO.StringWriter();
            using (var tee = new TeeTextWriter(console, new ThrowingTextWriter()))
            {
                tee.WriteLine("ligne critique");   // ne doit PAS jeter malgré le fichier en échec
                tee.Write('x');
            }
            Assert.Contains("ligne critique", console.ToString());   // la sortie primaire est intacte
            Assert.Contains("x", console.ToString());
        }

        /// <summary>TextWriter qui jette sur toute écriture — simule un disque en échec pour vérifier le best-effort.</summary>
        private sealed class ThrowingTextWriter : System.IO.TextWriter
        {
            public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
            public override void Write(char value) => throw new System.IO.IOException("disque simulé KO");
            public override void Write(string value) => throw new System.IO.IOException("disque simulé KO");
            public override void WriteLine(string value) => throw new System.IO.IOException("disque simulé KO");
        }
    }
}
