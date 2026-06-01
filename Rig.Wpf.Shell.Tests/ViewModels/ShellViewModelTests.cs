using System;
using FluentAssertions;
using Moq;
using Rig.Wpf.Core.Abstractions;
using Rig.Wpf.Mvvm.Processus;
using Rig.Wpf.Shell.ViewModels;
using Xunit;

namespace Rig.Wpf.Shell.Tests.ViewModels;

public class ShellViewModelTests
{
    private static readonly LegacyPluginDescriptor SampleDescriptor =
        new("TESTNLH", @"C:\rig\Bin Processus\PROC_TESTNLH.dll", typeof(SampleForm), Libelle: null);

    private sealed class SampleForm { }

    private sealed class FakeNativeProcessus : ProcessusViewModelBase
    {
        public FakeNativeProcessus(string code)
            : base(code, code, Array.Empty<EtapeViewModelBase>()) { }
    }

    private static (ShellViewModel Sut, Mock<IPluginLoader> Loader, Mock<INativeProcessusRegistry> Registry)
        CreateSut(params string[] nativeCodes)
    {
        var loader = new Mock<IPluginLoader>();
        loader.Setup(l => l.LoadPlugin(It.IsAny<string>()))
              .Returns<string>(c =>
                  c == "TESTNLH"
                      ? SampleDescriptor
                      : new LegacyPluginDescriptor(c, "x.dll", typeof(SampleForm), null));

        var registry = new Mock<INativeProcessusRegistry>();
        registry.Setup(r => r.IsNative(It.IsAny<string>()))
                .Returns<string>(c => Array.IndexOf(nativeCodes, c) >= 0);
        registry.Setup(r => r.CreateProcessus(It.IsAny<string>()))
                .Returns<string>(c => new FakeNativeProcessus(c));

        var sut = new ShellViewModel(loader.Object, registry.Object);
        return (sut, loader, registry);
    }

    [Fact]
    public void NewInstance_HasNoTabs_AndNullSelection()
    {
        var (sut, _, _) = CreateSut();

        sut.Tabs.Should().BeEmpty();
        sut.SelectedTab.Should().BeNull();
    }

    [Fact]
    public void OpenTab_AddsLegacyPluginTab_WhenCodeNotInRegistry()
    {
        var (sut, loader, _) = CreateSut();

        sut.OpenTabCommand.Execute("TESTNLH");

        sut.Tabs.Should().ContainSingle()
            .Which.Should().BeOfType<LegacyPluginTabViewModel>();
        loader.Verify(l => l.LoadPlugin("TESTNLH"), Times.Once);
    }

    [Fact]
    public void OpenTab_AddsNativeTab_WhenCodeIsRegistered()
    {
        var (sut, loader, registry) = CreateSut(nativeCodes: "TESTNLH");

        sut.OpenTabCommand.Execute("TESTNLH");

        sut.Tabs.Should().ContainSingle()
            .Which.Should().BeOfType<NativeTabViewModel>();
        loader.Verify(l => l.LoadPlugin(It.IsAny<string>()), Times.Never);
        registry.Verify(r => r.CreateProcessus("TESTNLH"), Times.Once);
    }

    [Fact]
    public void OpenTab_SetsSelectedToNewTab()
    {
        var (sut, _, _) = CreateSut();

        sut.OpenTabCommand.Execute("TESTNLH");

        sut.SelectedTab.Should().Be(sut.Tabs[0]);
    }

    [Fact]
    public void OpenTab_DoesNotDuplicate_WhenAlreadyOpen()
    {
        var (sut, loader, _) = CreateSut();

        sut.OpenTabCommand.Execute("TESTNLH");
        sut.OpenTabCommand.Execute("TESTNLH");

        sut.Tabs.Should().HaveCount(1);
        loader.Verify(l => l.LoadPlugin("TESTNLH"), Times.Once);
    }

    [Fact]
    public void OpenTab_OnExisting_FocusesIt()
    {
        var (sut, _, _) = CreateSut(nativeCodes: "OTHER");
        sut.OpenTabCommand.Execute("TESTNLH");
        sut.OpenTabCommand.Execute("OTHER");
        sut.SelectedTab!.CodeProcessus.Should().Be("OTHER");

        sut.OpenTabCommand.Execute("TESTNLH");

        sut.SelectedTab!.CodeProcessus.Should().Be("TESTNLH");
        sut.Tabs.Should().HaveCount(2);
    }

    [Fact]
    public void CloseTab_RemovesTab_AndUpdatesSelection()
    {
        var (sut, _, _) = CreateSut(nativeCodes: "OTHER");
        sut.OpenTabCommand.Execute("TESTNLH");
        sut.OpenTabCommand.Execute("OTHER");

        sut.CloseTabCommand.Execute("TESTNLH");

        sut.Tabs.Should().ContainSingle()
            .Which.CodeProcessus.Should().Be("OTHER");
        sut.SelectedTab!.CodeProcessus.Should().Be("OTHER");
    }

    [Fact]
    public void UpdateTabCaption_UpdatesLibelleOfMatchingTab()
    {
        var (sut, _, _) = CreateSut();
        sut.OpenTabCommand.Execute("TESTNLH");

        sut.UpdateTabCaption("TESTNLH", "Mon nouveau titre");

        sut.Tabs[0].Libelle.Should().Be("Mon nouveau titre");
    }

    [Fact]
    public void UpdateTabCaption_OnUnknownCode_IsNoOp()
    {
        var (sut, _, _) = CreateSut();
        sut.OpenTabCommand.Execute("TESTNLH");

        sut.UpdateTabCaption("INCONNU", "X");

        sut.Tabs[0].Libelle.Should().NotBe("X");
    }

    [Fact]
    public void OpenTab_WithEmptyCode_Throws()
    {
        var (sut, _, _) = CreateSut();

        FluentActions.Invoking(() => sut.OpenTabCommand.Execute(" "))
            .Should().Throw<ArgumentException>();
    }
}
