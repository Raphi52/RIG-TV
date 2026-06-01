using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Rig.Wpf.Core.Abstractions;
using Rig.Wpf.Core.DependencyInjection;
using Rig.Wpf.LegacyHost.DependencyInjection;
using Rig.Wpf.LegacyHost.Plugins;
using Xunit;

namespace Rig.Wpf.LegacyHost.Tests.DependencyInjection;

public class LegacyHostServiceCollectionExtensionsTests
{
    [Fact]
    public void AddRigWpfLegacyHost_ReplacesNullPluginLoader_WithAssemblyPluginLoader()
    {
        var services = new ServiceCollection();
        services.AddRigWpfCore();
        services.AddRigWpfLegacyHost(opts => opts.PluginDirectory = @"C:\does\not\matter");
        var provider = services.BuildServiceProvider();

        var loader = provider.GetRequiredService<IPluginLoader>();

        loader.Should().BeOfType<AssemblyPluginLoader>();
    }

    [Fact]
    public void AddRigWpfLegacyHost_PluginLoader_IsSingleton()
    {
        var services = new ServiceCollection();
        services.AddRigWpfCore();
        services.AddRigWpfLegacyHost();
        var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IPluginLoader>();
        var second = provider.GetRequiredService<IPluginLoader>();

        first.Should().BeSameAs(second);
    }
}
