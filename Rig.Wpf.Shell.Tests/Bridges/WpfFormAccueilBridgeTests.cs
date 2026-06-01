using System;
using FluentAssertions;
using Moq;
using Rig.Wpf.Core.Abstractions;
using Rig.Wpf.Mvvm;
using Rig.Wpf.Mvvm.Processus;
using Rig.Wpf.Shell.Bridges;
using Rig.Wpf.Shell.ViewModels;
using Xunit;

namespace Rig.Wpf.Shell.Tests.Bridges;

public class WpfFormAccueilBridgeTests
{
    private static readonly LegacyPluginDescriptor SampleDescriptor =
        new("TESTNLH", "x.dll", typeof(object), null);

    private static (WpfFormAccueilBridge Bridge, ShellViewModel Shell) CreateSut()
    {
        var loader = new Mock<IPluginLoader>();
        loader.Setup(l => l.LoadPlugin(It.IsAny<string>()))
              .Returns<string>(c => new LegacyPluginDescriptor(c, "x.dll", typeof(object), null));
        var registry = new Mock<INativeProcessusRegistry>();
        registry.Setup(r => r.IsNative(It.IsAny<string>())).Returns(false);
        var shell = new ShellViewModel(loader.Object, registry.Object);
        IDispatcherService dispatcher = new SynchronousDispatcher();
        var bridge = new WpfFormAccueilBridge(shell, dispatcher);
        return (bridge, shell);
    }

    [Fact]
    public void ChangeTabText_UpdatesTabLibelle()
    {
        var (bridge, shell) = CreateSut();
        shell.OpenTabCommand.Execute("TESTNLH");

        bridge.ChangeTabText("TESTNLH", "Mon titre");

        shell.Tabs[0].Libelle.Should().Be("Mon titre");
    }

    [Fact]
    public void ChangeTabText_OnUnknownCode_DoesNotThrow()
    {
        var (bridge, _) = CreateSut();

        FluentActions.Invoking(() => bridge.ChangeTabText("INCONNU", "X"))
            .Should().NotThrow();
    }

    [Fact]
    public void IsTabOpen_ReturnsTrue_AfterOpenTab()
    {
        var (bridge, shell) = CreateSut();
        shell.OpenTabCommand.Execute("TESTNLH");

        bridge.IsTabOpen("TESTNLH").Should().BeTrue();
        bridge.IsTabOpen("AUTRE").Should().BeFalse();
    }

    [Fact]
    public void CloseTab_RemovesTabFromShell()
    {
        var (bridge, shell) = CreateSut();
        shell.OpenTabCommand.Execute("TESTNLH");

        bridge.CloseTab("TESTNLH");

        shell.Tabs.Should().BeEmpty();
    }

    [Fact]
    public void Bridge_UsesDispatcher_ToMarshalToUiThread()
    {
        var dispatcher = new Mock<IDispatcherService>();
        dispatcher.Setup(d => d.Invoke(It.IsAny<Action>()))
                  .Callback<Action>(a => a());
        var loader = new Mock<IPluginLoader>();
        loader.Setup(l => l.LoadPlugin(It.IsAny<string>()))
              .Returns<string>(c => new LegacyPluginDescriptor(c, "x.dll", typeof(object), null));
        var registry = new Mock<INativeProcessusRegistry>();
        registry.Setup(r => r.IsNative(It.IsAny<string>())).Returns(false);
        var shell = new ShellViewModel(loader.Object, registry.Object);
        shell.OpenTabCommand.Execute("TESTNLH");
        var bridge = new WpfFormAccueilBridge(shell, dispatcher.Object);

        bridge.ChangeTabText("TESTNLH", "X");

        dispatcher.Verify(d => d.Invoke(It.IsAny<Action>()), Times.AtLeastOnce);
    }
}
