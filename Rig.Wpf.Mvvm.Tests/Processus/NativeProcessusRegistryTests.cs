using System;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Rig.Wpf.Mvvm.Processus;
using Xunit;

namespace Rig.Wpf.Mvvm.Tests.Processus;

public class NativeProcessusRegistryTests
{
    private sealed class StubProcessus : ProcessusViewModelBase
    {
        public StubProcessus()
            : base("STUB", "Stub processus", Array.Empty<EtapeViewModelBase>()) { }
    }

    [Fact]
    public void EmptyRegistry_HasNoNativeCodes()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var registry = new NativeProcessusRegistry(sp);

        registry.IsNative("ANYTHING").Should().BeFalse();
    }

    [Fact]
    public void Register_MakesCodeNative()
    {
        var services = new ServiceCollection();
        services.AddTransient<StubProcessus>();
        var sp = services.BuildServiceProvider();
        var registry = new NativeProcessusRegistry(sp);
        registry.Register("STUB", typeof(StubProcessus));

        registry.IsNative("STUB").Should().BeTrue();
        registry.IsNative("stub").Should().BeTrue("la comparaison est insensible à la casse");
    }

    [Fact]
    public void CreateProcessus_ResolvesViaServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddTransient<StubProcessus>();
        var sp = services.BuildServiceProvider();
        var registry = new NativeProcessusRegistry(sp);
        registry.Register("STUB", typeof(StubProcessus));

        var vm = registry.CreateProcessus("STUB");

        vm.Should().BeOfType<StubProcessus>();
        vm.Code.Should().Be("STUB");
    }

    [Fact]
    public void CreateProcessus_OnUnknownCode_Throws()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var registry = new NativeProcessusRegistry(sp);

        FluentActions.Invoking(() => registry.CreateProcessus("INCONNU"))
            .Should().Throw<InvalidOperationException>();
    }
}
