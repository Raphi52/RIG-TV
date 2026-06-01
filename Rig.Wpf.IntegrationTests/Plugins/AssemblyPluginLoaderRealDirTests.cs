using System.IO;
using FluentAssertions;
using Rig.Wpf.LegacyHost.Plugins;
using Xunit;

namespace Rig.Wpf.IntegrationTests.Plugins;

/// <summary>
/// Tests d'intégration qui vérifient le comportement de l'<see cref="AssemblyPluginLoader"/>
/// face au dossier réel <c>C:\rig\Bin Processus</c>. Skippés si le dossier n'est pas présent.
/// </summary>
[Trait("Category", "Integration")]
public class AssemblyPluginLoaderRealDirTests
{
    private const string RealPluginDir = @"C:\rig\Bin Processus";

    [SkippableFact]
    public void Discover_OnRealBinProcessus_ReturnsAtLeastOneDescriptor()
    {
        Skip.IfNot(Directory.Exists(RealPluginDir),
            $"Dossier '{RealPluginDir}' absent — environnement non provisionné.");

        var loader = new AssemblyPluginLoader(new PluginLoaderOptions
        {
            PluginDirectory = RealPluginDir
        });

        var descriptors = loader.Discover();

        descriptors.Should().NotBeEmpty(
            "le dossier C:\\rig\\Bin Processus est censé contenir des PROC_*.dll");
    }
}
