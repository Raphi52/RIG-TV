using System.IO;
using Rig.Wpf.Kbis.TestViewer.Services;
using Xunit;

namespace Rig.Rapture.Tests
{
    /// <summary>
    /// Garde anti-régression du bug « tuile figée sur Running » (DCADEMAT, 2026-06-24) : le préfixe
    /// Stamp() « [hh:mm:ss.fff] [snap=NNNN] » émis par SmokeRunner doit être STRIPPÉ du Description parsé
    /// par SmokeRunnerProxy, sinon le Description.StartsWith("{TAG} : ...") des adapters legacy ne matche
    /// jamais le step terminal → ComputePhase reste éternellement Running.
    /// </summary>
    public class SmokeResultParsingTests
    {
        [Fact]
        public void AppendLineForParsing_strips_stamp_so_adapter_StartsWith_matches()
        {
            var proxy = new SmokeRunnerProxy("nonexistent.exe", Path.GetTempPath());
            proxy.AppendLineForParsing(
                "  ✓ [10:21:55.924] [snap=0044] DCA-VALIDATION : Action (validation) - case DCA + Valider",
                "dcademat-dca-validation");

            var line = Assert.Single(proxy.Lines);
            Assert.Equal(SmokeOutcome.Passed, line.Outcome);
            Assert.Equal("dcademat-dca-validation", line.WorkerId);
            // Contrat des adapters legacy : le terminal est détecté par Description.StartsWith("{TAG} : ...").
            Assert.StartsWith("DCA-VALIDATION : Action", line.Description);
        }

        [Fact]
        public void AppendLineForParsing_without_stamp_still_parses()
        {
            var proxy = new SmokeRunnerProxy("nonexistent.exe", Path.GetTempPath());
            proxy.AppendLineForParsing("  ✗ VK : Le PDF K-bis contient les bonnes données", "kbis-vk");

            var line = Assert.Single(proxy.Lines);
            Assert.Equal(SmokeOutcome.Failed, line.Outcome);
            Assert.StartsWith("VK :", line.Description);
        }

        [Fact]
        public void AppendLineForParsing_ignores_non_result_lines()
        {
            var proxy = new SmokeRunnerProxy("nonexistent.exe", Path.GetTempPath());
            proxy.AppendLineForParsing("      → un détail de log sans icône", "kbis-vk");
            Assert.Empty(proxy.Lines);
        }
    }
}
