using System;
using System.IO;
using FluentAssertions;
using Rig.Wpf.LegacyHost.Plugins;
using Rig.Wpf.TestHelpers;
using Xunit;

namespace Rig.Wpf.LegacyHost.Tests.Plugins;

public class AssemblyPluginLoaderTests : IDisposable
{
    private readonly string _pluginDir;

    public AssemblyPluginLoaderTests()
    {
        _pluginDir = Path.Combine(
            Path.GetTempPath(),
            "Rig.Wpf.LoaderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pluginDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_pluginDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private AssemblyPluginLoader CreateSut()
        => new(new PluginLoaderOptions { PluginDirectory = _pluginDir });

    [Fact]
    public void Discover_OnEmptyDirectory_ReturnsEmpty()
    {
        var sut = CreateSut();

        sut.Discover().Should().BeEmpty();
    }

    [Fact]
    public void Discover_OnMissingDirectory_ReturnsEmpty()
    {
        var sut = new AssemblyPluginLoader(new PluginLoaderOptions
        {
            PluginDirectory = Path.Combine(_pluginDir, "missing")
        });

        sut.Discover().Should().BeEmpty();
    }

    [Fact]
    public void Discover_WithStubPlugins_ReturnsAllCodes()
    {
        StubPluginFactory.CreatePluginAssembly(_pluginDir, "TESTNLH");
        StubPluginFactory.CreatePluginAssembly(_pluginDir, "DEMO");
        var sut = CreateSut();

        var descriptors = sut.Discover();

        descriptors.Should().HaveCount(2);
        descriptors.Should().Contain(d => d.CodeProcessus == "TESTNLH");
        descriptors.Should().Contain(d => d.CodeProcessus == "DEMO");
    }

    [Fact]
    public void Discover_DoesNotInstantiateForms()
    {
        StubPluginFactory.CreatePluginAssembly(_pluginDir, "TESTNLH");
        var sut = CreateSut();

        var descriptors = sut.Discover();

        // Le descriptor expose juste le Type ; aucune instanciation côté loader.
        descriptors.Should().ContainSingle()
            .Which.FormType.FullName.Should().Be("RIG.PROCESSUS.FORM_TESTNLH");
    }

    [Fact]
    public void LoadPlugin_OnExistingStub_ReturnsDescriptor()
    {
        StubPluginFactory.CreatePluginAssembly(_pluginDir, "TESTNLH");
        var sut = CreateSut();

        var descriptor = sut.LoadPlugin("TESTNLH");

        descriptor.CodeProcessus.Should().Be("TESTNLH");
        descriptor.FormType.FullName.Should().Be("RIG.PROCESSUS.FORM_TESTNLH");
        descriptor.DllPath.Should().EndWith("PROC_TESTNLH.dll");
    }

    [Fact]
    public void LoadPlugin_OnUnknownCode_Throws()
    {
        var sut = CreateSut();

        FluentActions.Invoking(() => sut.LoadPlugin("INCONNU"))
            .Should().Throw<PluginLoadException>()
            .WithMessage("*INCONNU*");
    }

    [Fact]
    public void LoadPlugin_OnEmptyCode_Throws()
    {
        var sut = CreateSut();

        FluentActions.Invoking(() => sut.LoadPlugin(" "))
            .Should().Throw<ArgumentException>()
            .WithParameterName("codeProcessus");
    }
}
