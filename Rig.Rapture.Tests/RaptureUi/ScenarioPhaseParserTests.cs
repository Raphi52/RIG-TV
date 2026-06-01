using FluentAssertions;
using Rig.Wpf.Kbis.TestViewer.Services;
using Rig.Wpf.Kbis.TestViewer.ViewModels;
using Xunit;

namespace Rig.Rapture.Tests.RaptureUi;

public class ScenarioPhaseParserTests
{
    [Theory]
    [InlineData("      → App ouverte post-login en 3,2s", ScenarioPhase.Login)]
    [InlineData("      → PostMessage SC_MAXIMIZE sur FormAccueil (HDESK isole)", ScenarioPhase.Login)]
    [InlineData("  ✓ Open PROC_RETAUD", ScenarioPhase.OpenProcRetaud)]
    [InlineData("      → Sélection audience 20/05/2026 14:30", ScenarioPhase.SelectAudience)]
    [InlineData("      → Click 'Importer Rapture'", ScenarioPhase.ClickImporter)]
    [InlineData("      → Recap : 54 rows, 0 errors", ScenarioPhase.Recap)]
    [InlineData("      → Click 'Importer (N champs cochés)'", ScenarioPhase.Apply)]
    public void Parse_MapsKnownMarkerToPhase(string line, ScenarioPhase expected)
    {
        ScenarioPhaseParser.TryParse(line, out var phase).Should().BeTrue();
        phase.Should().Be(expected);
    }

    [Fact]
    public void Parse_UnknownLine_ReturnsFalse()
    {
        ScenarioPhaseParser.TryParse("random log without marker", out var phase).Should().BeFalse();
        phase.Should().Be(ScenarioPhase.Queued);
    }

    [Fact]
    public void Parse_FailLine_ReturnsFailPhase()
    {
        ScenarioPhaseParser.TryParse("      Exception: OpenFileDialog pas apparu après 30s click 'Importer Rapture'", out var phase).Should().BeTrue();
        phase.Should().Be(ScenarioPhase.Fail);
    }
}
