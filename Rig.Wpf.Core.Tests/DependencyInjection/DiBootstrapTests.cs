using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Rig.Wpf.Core.Abstractions;
using Rig.Wpf.Core.DependencyInjection;
using Xunit;

namespace Rig.Wpf.Core.Tests.DependencyInjection;

public class DiBootstrapTests
{
    [Fact]
    public void AddRigWpfCore_RegistersISessionContext()
    {
        var services = new ServiceCollection();

        services.AddRigWpfCore();
        var provider = services.BuildServiceProvider();

        provider.GetService<ISessionContext>().Should().NotBeNull();
    }

    [Fact]
    public void AddRigWpfCore_RegistersIUtilisateurService()
    {
        var services = new ServiceCollection();

        services.AddRigWpfCore();
        var provider = services.BuildServiceProvider();

        provider.GetService<IUtilisateurService>().Should().NotBeNull();
    }

    [Fact]
    public void AddRigWpfCore_RegistersIGreffeService()
    {
        var services = new ServiceCollection();

        services.AddRigWpfCore();
        var provider = services.BuildServiceProvider();

        provider.GetService<IGreffeService>().Should().NotBeNull();
    }

    [Fact]
    public void AddRigWpfCore_RegistersIPluginLoader()
    {
        var services = new ServiceCollection();

        services.AddRigWpfCore();
        var provider = services.BuildServiceProvider();

        provider.GetService<IPluginLoader>().Should().NotBeNull();
    }

    [Fact]
    public void AddRigWpfCore_RegistersIFormAccueilBridge()
    {
        var services = new ServiceCollection();

        services.AddRigWpfCore();
        var provider = services.BuildServiceProvider();

        provider.GetService<IFormAccueilBridge>().Should().NotBeNull();
    }

    [Fact]
    public void ISessionContext_IsRegisteredAsSingleton()
    {
        var services = new ServiceCollection();

        services.AddRigWpfCore();
        var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<ISessionContext>();
        var second = provider.GetRequiredService<ISessionContext>();
        first.Should().BeSameAs(second);
    }
}
