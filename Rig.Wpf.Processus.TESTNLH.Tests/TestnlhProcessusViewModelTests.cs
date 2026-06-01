using FluentAssertions;
using Rig.Wpf.Processus.Testnlh;
using Rig.Wpf.Processus.Testnlh.Etapes;
using Xunit;

namespace Rig.Wpf.Processus.Testnlh.Tests;

public class TestnlhProcessusViewModelTests
{
    [Fact]
    public void NewInstance_Has_TESTNLH_Code()
    {
        var sut = new TestnlhProcessusViewModel();

        sut.Code.Should().Be("TESTNLH");
    }

    [Fact]
    public void NewInstance_HasOneEtape_OfTypeEtape1()
    {
        var sut = new TestnlhProcessusViewModel();

        sut.Etapes.Should().ContainSingle()
            .Which.Should().BeOfType<TestnlhEtape1ViewModel>();
        sut.CurrentEtape.Should().BeSameAs(sut.Etapes[0]);
    }

    [Fact]
    public void GoNextCommand_IsAlwaysFalse_WhenSingleEtape()
    {
        var sut = new TestnlhProcessusViewModel();

        sut.GoNextCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void SaveCommand_IsDisabled_WhenEtapeInvalid()
    {
        var sut = new TestnlhProcessusViewModel();

        sut.SaveCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void SaveCommand_BecomesEnabled_WhenMessageProvided()
    {
        var sut = new TestnlhProcessusViewModel();
        var etape = (TestnlhEtape1ViewModel)sut.Etapes[0];
        var raised = 0;
        sut.SaveCommand.CanExecuteChanged += (_, _) => raised++;

        etape.Message = "OK";

        sut.SaveCommand.CanExecute(null).Should().BeTrue();
        raised.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SaveCommand_Execute_StoresLastSavedMessage()
    {
        var sut = new TestnlhProcessusViewModel();
        var etape = (TestnlhEtape1ViewModel)sut.Etapes[0];
        etape.Message = "Bonjour le monde";

        sut.SaveCommand.Execute(null);

        sut.LastSavedMessage.Should().Be("Bonjour le monde");
    }
}
