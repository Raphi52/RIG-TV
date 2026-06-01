using System.Linq;
using FluentAssertions;
using Rig.Wpf.LegacyHost.Plugins;
using Xunit;

namespace Rig.Wpf.IntegrationTests.Plugins;

/// <summary>
/// Vérifie que la résolution de type est insensible à la casse (PROC_Mandataire.dll
/// contient FORM_MANDATAIRE) et que plusieurs plugins métier représentatifs
/// chargent leur descriptor sans crash. Capture par le test ce qui a été
/// validé manuellement par le litmus test de cohabitation.
/// </summary>
[Trait("Category", "Integration")]
public class LegacyCohabitationSuiteTests
{
    private const string RealPluginDir = @"C:\rig\Bin Processus";

    // Échantillon représentatif (tailles 8-18 KB) — confirmé par litmus test
    // qu'ils chargent en mode legacy sans crash.
    private static readonly string[] SampleCodes =
    {
        "Mandataire", "PARTIES", "FNIG", "DEPOTJUD", "VINS", "CNI", "XEX"
    };

    [SkippableTheory]
    [InlineData("Mandataire", "FORM_MANDATAIRE")]
    [InlineData("PARTIES", "FORM_PARTIES")]
    [InlineData("VINS", "FORM_VINS")]
    [InlineData("CNI", "FORM_CNI")]
    [InlineData("DEPOTJUD", "FORM_DEPOTJUD")]
    [InlineData("FNIG", "FORM_FNIG")]
    [InlineData("XEX", "FORM_XEX")]
    public void LoadPlugin_ResolvesType_CaseInsensitive(string code, string expectedTypeName)
    {
        Skip.IfNot(System.IO.Directory.Exists(RealPluginDir),
            "C:\\rig\\Bin Processus absent.");

        var loader = new AssemblyPluginLoader(new PluginLoaderOptions
        {
            PluginDirectory = RealPluginDir
        });

        var descriptor = loader.LoadPlugin(code);

        descriptor.CodeProcessus.Should().Be(code);
        descriptor.FormType.Name.Should().Be(expectedTypeName);
        descriptor.FormType.FullName.Should().StartWith("RIG.PROCESSUS.");
    }

    [SkippableFact]
    public void Discover_OnRealBinProcessus_FindsAllSampleCodes()
    {
        Skip.IfNot(System.IO.Directory.Exists(RealPluginDir),
            "C:\\rig\\Bin Processus absent.");

        var loader = new AssemblyPluginLoader(new PluginLoaderOptions
        {
            PluginDirectory = RealPluginDir
        });

        var discovered = loader.Discover()
            .Select(d => d.CodeProcessus)
            .ToHashSet(System.StringComparer.OrdinalIgnoreCase);

        foreach (var code in SampleCodes)
        {
            discovered.Should().Contain(code,
                $"le plugin {code} devrait être découvert (litmus test confirmé)");
        }
    }
}
